using System.Diagnostics;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.ShelfReading;

/// <summary>What a shelf read answers: the rows it added, then the phone's rows it skipped, and how many items the reader read (for the log; never their words).</summary>
public sealed record ShelfReadAnswer(CandidatesDto Candidates, int ItemsRead);

/// <summary>
/// Use case: the phone has had <c>identify</c> answer a shelf snap, and now sends the same picture for the server's own reader (ADR-033).
/// The picture is read once, in memory: it is never stored and nothing of it outlives the request. What the reader finds is grounded
/// as <c>identify</c> grounds a read, and <see cref="PcShelfUpgrade"/> settles the snap, server first (#814): the server's finds are
/// added (<see cref="CandidateSource.PictureServerModel"/>, unconfirmed: a person ticks them in Review, nothing adds them by itself),
/// except one whose release a row of the snap already has, which stays the row for it; the phone's rows the rule leaves out become
/// server skips (<see cref="SkipReason.ServerDidNotSee"/>, with Put back), never a row the person acted on. A snap is read once;
/// sending it again changes nothing more and answers the same rows.
/// </summary>
public static class ReadShelfSnap
{
    public sealed record Command(Guid SessionId, Guid SnapId, byte[] Jpeg);

    public sealed class Handler(
        IAppDbContext db,
        IIdGenerator ids,
        ICurrentUser currentUser,
        TimeProvider clock,
        ISessionLock sessionLock,
        IdentifyGrounding grounding,
        CandidateResolver resolver,
        IJobQueue jobs,
        ScanningOptions options,
        IGameSerialLookup serials,
        IShelfReaderSetup setup,
        IShelfReader reader,
        IShelfStillMaker stills,
        ShelfReadGate gate,
        ILogger<Handler> logger)
    {
        /// <summary>The most items one reading may add; a reader that lists more is cut off here (the same cap <c>identify</c> has).</summary>
        private const int MaxItems = IdentifyCleaning.MaxItems;

        public async Task<Result<ShelfReadAnswer>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("ReadShelfSnap needs a signed-in caller; the endpoint requires authorization.");

            if (await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken) is not { } session)
            {
                return ScanningErrors.SessionNotFound;
            }

            // No reader set up: nothing below is touched (not the still maker, not the gate, not the reader).
            if (!setup.IsSetUp)
            {
                return ShelfReadErrors.ReaderOff;
            }

            if (session.Status != ScanSessionStatus.Open)
            {
                return ScanningErrors.SessionClosed;
            }

            // The snap exists only once identify has answered it.
            var snap = await db.ScanSnaps.FirstOrDefaultAsync(s => s.Id == command.SnapId && s.SessionId == session.Id, cancellationToken);
            if (snap is null)
            {
                return ShelfReadErrors.SnapNotFound;
            }

            if (snap.ShotKind is ShotKind.SingleCover or ShotKind.LooseItem or ShotKind.Barcode || snap.AssetId is not null)
            {
                return ShelfReadErrors.NotAShelf;
            }

            if (snap.ServerReadAt is not null)
            {
                return await RowsOfAsync(session, snap, cancellationToken);
            }

            // The reader does one picture at a time. Whoever leaves this block (an answer, a dropped request) frees it.
            ShelfReadResult read;
            var turn = await gate.EnterAsync(cancellationToken);
            if (turn is null)
            {
                return ShelfReadErrors.ReaderBusy;
            }

            try
            {
                // A twin of this request may have read the snap while this one waited for its turn.
                await db.ScanSnaps.Entry(snap).ReloadAsync(cancellationToken);
                if (snap.ServerReadAt is not null)
                {
                    return await RowsOfAsync(session, snap, cancellationToken);
                }

                var still = stills.Make(command.Jpeg);
                if (still is null)
                {
                    return JpegSanitizer.Damaged;
                }

                read = await reader.ReadAsync(still, cancellationToken);
            }
            finally
            {
                turn.Dispose();
            }

            if (read.Status == ShelfReadStatus.Off)
            {
                return ShelfReadErrors.ReaderOff;
            }

            if (!PcShelfUpgrade.IsUsable(read.Status))
            {
                return ShelfReadErrors.ReaderUnavailable; // nothing was changed; the snap can be sent again
            }

            return await KeepAsync(session, snap, read.Items, userId, cancellationToken);
        }

        /// <summary>
        /// Grounds what the reader found and lets <see cref="PcShelfUpgrade"/> settle the snap: the server's finds stand (unconfirmed, for
        /// review) except one whose release a row of the snap already has, which stays the row for it; the phone's rows the rule leaves out
        /// become server skips (<see cref="SkipReason.ServerDidNotSee"/>) unless the person acted on them.
        /// </summary>
        private async Task<Result<ShelfReadAnswer>> KeepAsync(
            ScanSession session, ScanSnap snap, IReadOnlyList<ShelfReadItem> found, Guid userId, CancellationToken cancellationToken)
        {
            var reads = found.Where(item => !string.IsNullOrWhiteSpace(item.Title)).Take(MaxItems).Select(ServerReadInput.ReadOf).ToList();
            var items = IdentifyCleaning.Clean(reads, IdentifyKind.Shelf, serials);
            var answers = await GroundAsync(items, cancellationToken);

            // An unfound spine has nothing to review (UnfoundSpines), as in the eval row: it is a skipped read, which the rule never sees.
            var unfound = UnfoundSpines.Of([.. items.Select((item, i) => new UnfoundRead(item.Profile, answers[i].Verdict))]).ToHashSet();
            var made = new List<ScanCandidate>();
            var madeFacts = new List<ReadFacts>();
            for (var i = 0; i < items.Count; i++)
            {
                if (unfound.Contains(i))
                {
                    continue;
                }

                var item = items[i];
                var candidate = new ScanCandidate
                {
                    Id = ids.NewId(),
                    LibraryId = session.LibraryId,
                    SessionId = session.Id,
                    Source = CandidateSource.PictureServerModel,
                    ShotKind = snap.ShotKind,
                    VisibleText = item.Facts.ShownText,
                    SnapId = snap.Id,
                    Resolution = CandidateResolution.Pending,
                    Decision = CandidateDecision.Undecided,
                    InferredJson = StoredRead.Serialize(item.Facts, item.Profile, unconfirmed: true), // a row of its own is never confirmed: see Settle
                    IdempotencyKey = $"snap:{snap.Id:N}:server:{i}",
                    CreatedAt = clock.GetUtcNow(),
                };

                // Written onto the row as identify writes an answer, so the best release is what the row will show.
                await resolver.ApplyAsync(session.LibraryId, candidate, answers[i], cancellationToken);
                made.Add(candidate);
                madeFacts.Add(item.Facts);
            }

            var added = new List<ScanCandidate>();
            var skipped = new List<ScanCandidate>();
            await using (await sessionLock.AcquireAsync(session.Id, cancellationToken))
            {
                await db.ScanSessions.Entry(session).ReloadAsync(cancellationToken);
                if (db.ScanSessions.Entry(session).State == EntityState.Detached)
                {
                    return ScanningErrors.SessionNotFound; // cleaned up mid-read; the finds die with the session
                }

                if (session.Status != ScanSessionStatus.Open)
                {
                    return ScanningErrors.SessionClosed;
                }

                await db.ScanSnaps.Entry(snap).ReloadAsync(cancellationToken);
                if (snap.ServerReadAt is not null)
                {
                    return await RowsOfAsync(session, snap, cancellationToken);
                }

                // The rows the phone's identify made for this snap, read fresh under the lock: what the person did with them is what counts.
                var phoneRows = await db.ScanCandidates
                    .Where(c => c.SessionId == session.Id && c.SnapId == snap.Id
                        && (c.Source == CandidateSource.PictureText || c.Source == CandidateSource.PictureAppleModel))
                    .OrderBy(c => c.Seq)
                    .ToListAsync(cancellationToken);
                var settled = Settle(made, madeFacts, phoneRows, await KindsAsync([.. made, .. phoneRows], cancellationToken));

                var nextSeq = (await db.ScanCandidates.Where(c => c.SessionId == session.Id).MaxAsync(c => (int?)c.Seq, cancellationToken) ?? 0) + 1;
                foreach (var candidate in settled.Added)
                {
                    db.ScanCandidates.Add(candidate);
                    db.ScanCandidates.Entry(candidate).Property(c => c.Seq).CurrentValue = nextSeq++; // Seq is history, so it is written through EF
                    added.Add(candidate);
                }

                foreach (var row in settled.LeftOut)
                {
                    CandidateSkip.Apply(row, ServerDidNotSee.StepName);
                    skipped.Add(row);
                }

                snap.ServerReadAt = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
            }

            // A read the providers ran out of time for is finished by the same job that finishes identify's.
            var pending = added.Where(c => c.Resolution == CandidateResolution.Pending)
                .Select(c => new JobToEnqueue(
                    new IdentifyItems.ResolveJob(session.LibraryId, session.Id, c.Id, userId),
                    new JobEnqueueOptions(null, $"{IdentifyItems.ResolveJobType}:{c.Id}")))
                .ToList();
            if (pending.Count > 0)
            {
                await jobs.EnqueueManyAsync(IdentifyItems.ResolveJobType, pending, cancellationToken);
            }

            return new ShelfReadAnswer(new CandidatesDto(await CandidateProjection.ToDtosAsync(db, [.. added, .. skipped], cancellationToken)), found.Count);
        }

        /// <summary>What the read does to the snap: the server's rows to add, and the phone's rows that become server skips.</summary>
        private sealed record Settled(List<ScanCandidate> Added, List<ScanCandidate> LeftOut);

        /// <summary>
        /// <see cref="PcShelfUpgrade"/> over the snap. The phone's rows are told apart by how identify stored them: a row whose read is
        /// marked unconfirmed is one only the phone's model read (M: the phone marks a model read Vision has nothing at; identify clears
        /// the mark where the two engines read the same release at one place); every other picture row is Vision's (V). A row identify
        /// skipped (a repeat, a lost place, a fragment, an unfound spine) is a skipped read, which the rule never sees, as in the eval.
        /// A server row whose release a phone row already has is not added again: that row is the row (a Vision row is confirmed already;
        /// a model row stays unconfirmed, as the server row would have been). A phone row the rule leaves out (a Vision row it does not
        /// keep, a model row) becomes a server skip, but only while it is the server's to change: still undecided or as the server
        /// decided it, not edited, not added, not kept as a provisional record, and answered (a row still Pending has no release to judge).
        /// </summary>
        private static Settled Settle(
            List<ScanCandidate> server, List<ReadFacts> serverFacts, List<ScanCandidate> phone, IReadOnlyDictionary<Guid, WorkKind> kinds)
        {
            var standing = phone.Where(row => !SkippedByTheReading(row)).ToList();
            var vision = standing.Where(row => !StoredRead.IsUnconfirmed(row.InferredJson)).ToList();
            var model = standing.Where(row => StoredRead.IsUnconfirmed(row.InferredJson)).ToList();
            var serverReads = server.Select((row, i) => ShelfReadOf(row, serverFacts[i], kinds)).ToList();
            var visionReads = vision.Select(row => ShelfReadOf(row, StoredRead.From(row).Facts, kinds)).ToList();
            var modelReads = model.Select(row => ShelfReadOf(row, StoredRead.From(row).Facts, kinds)).ToList();
            var decision = PcShelfUpgrade.Decide(serverReads, visionReads);

            var added = new List<ScanCandidate>();
            var standsIn = new HashSet<ScanCandidate>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < server.Count; i++)
            {
                // The row the phone already has for this release, Vision's first: the server's row is not shown twice.
                var key = serverReads[i].Release;
                var same = key is null ? null
                    : vision.Where((_, j) => visionReads[j].Release == key).FirstOrDefault()
                        ?? model.Where((_, j) => modelReads[j].Release == key).FirstOrDefault();
                if (same is null)
                {
                    added.Add(server[i]); // unconfirmed: had a Vision row named its release, that row would be the row
                }
                else
                {
                    standsIn.Add(same);
                }
            }

            var leftOut = vision.Where((row, j) => !decision.VisionKept[j] && !standsIn.Contains(row) && TheServersToChange(row))
                .Concat(model.Where(row => !standsIn.Contains(row) && TheServersToChange(row)))
                .OrderBy(row => row.Seq)
                .ToList();
            return new Settled(added, leftOut);
        }

        /// <summary>A row identify skipped as a read (a repeat, a lost place, a fragment, an unfound spine): no read of the shelf for the rule.</summary>
        private static bool SkippedByTheReading(ScanCandidate row) =>
            SkipReasons.Of(row.Decision, row.AlreadyOwnedCount, CandidateJson.ReadTrace(row.TraceJson))
                is SkipReason.Repeat or SkipReason.Place or SkipReason.Fragment or SkipReason.UnfoundSpine;

        /// <summary>
        /// Whether the server may still skip a phone row: the person has not decided it (it is undecided, or as the server's own default
        /// left it), changed it, added it or kept it as a provisional record, nothing added it by itself, and it has an answer.
        /// A row the server already skipped for another reason (the library owns it, it is on the wishlist) keeps that reason.
        /// </summary>
        private static bool TheServersToChange(ScanCandidate row)
        {
            var trace = CandidateJson.ReadTrace(row.TraceJson);
            var asTheServerLeftIt = row.Decision == CandidateDecision.Undecided
                || (row.Decision == CandidateDecision.Accepted && trace.AutoDecision == CandidateDecision.Accepted);
            return asTheServerLeftIt
                && !row.UserEdited
                && row.CreatedItemId is null
                && row.AutoCommittedItemIds is null
                && row.ProvisionalId is null
                && row.Resolution != CandidateResolution.Pending;
        }

        /// <summary>A row as the rule reads it: the text it was searched with, the kind it was read as, and the release it shows first.</summary>
        private static PcShelfUpgrade.ShelfRead ShelfReadOf(ScanCandidate row, ReadFacts facts, IReadOnlyDictionary<Guid, WorkKind> kinds)
        {
            var best = BestOf(row);
            return new(
                facts.SearchText,
                facts.Kind,
                best?.Title,
                best?.Year,
                best is not null && kinds.TryGetValue(best.ReleaseId, out var kind) ? kind : null);
        }

        private static StoredAlternative? BestOf(ScanCandidate row) =>
            CandidateJson.ReadAlternatives(row.AlternativesJson) is { Count: > 0 } alternatives ? alternatives[0] : null;

        /// <summary>The kind of the release each row shows first: from the catalogue, or from a release this request is adding (not saved yet).</summary>
        private async Task<IReadOnlyDictionary<Guid, WorkKind>> KindsAsync(IEnumerable<ScanCandidate> rows, CancellationToken cancellationToken)
        {
            var releaseIds = rows.Select(BestOf).OfType<StoredAlternative>().Select(best => best.ReleaseId).Distinct().ToList();
            var kinds = new Dictionary<Guid, WorkKind>(await ShotKindRule.LocalKindsAsync(db, releaseIds, cancellationToken));
            foreach (var releaseId in releaseIds.Where(id => !kinds.ContainsKey(id)))
            {
                if (db.Releases.Local.FirstOrDefault(r => r.Id == releaseId) is { } release
                    && db.Works.Local.FirstOrDefault(w => w.Id == release.WorkId) is { } work)
                {
                    kinds[releaseId] = work.Kind;
                }
            }

            return kinds;
        }

        /// <summary>
        /// What a second request for the same snap answers: the rows the first read added, in order, then the phone's rows it skipped
        /// (each as it is now: one the person put back since is shown as they left it).
        /// </summary>
        private async Task<Result<ShelfReadAnswer>> RowsOfAsync(ScanSession session, ScanSnap snap, CancellationToken cancellationToken)
        {
            var rows = await db.ScanCandidates.AsNoTracking()
                .Where(c => c.SessionId == session.Id && c.SnapId == snap.Id)
                .OrderBy(c => c.Seq)
                .ToListAsync(cancellationToken);
            var added = rows.Where(c => c.Source == CandidateSource.PictureServerModel);
            var skipped = rows.Where(c => c.Source != CandidateSource.PictureServerModel
                && CandidateJson.ReadTrace(c.TraceJson).Steps.Any(step => step.Provider == ServerDidNotSee.StepName));
            return new ShelfReadAnswer(new CandidatesDto(await CandidateProjection.ToDtosAsync(db, [.. added, .. skipped], cancellationToken)), 0);
        }
        /// <summary>
        /// Grounds the reads the way <c>identify</c> does, within its budget: the catalogue and the providers are asked for every read at
        /// once, a read that found nothing is asked again trimmed, then the series pass and the kind pass over the whole shelf. A read the
        /// budget runs out on comes back Pending.
        /// </summary>
        private async Task<List<BarcodeResolution>> GroundAsync(List<CleanItem> items, CancellationToken cancellationToken)
        {
            var answers = new BarcodeResolution[items.Count];
            var toAsk = new List<int>();
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Facts.SearchText is null)
                {
                    answers[i] = grounding.Score(items[i].Facts, items[i].Profile, LocalPool.Empty, ProviderAnswers.None);
                }
                else
                {
                    toAsk.Add(i);
                }
            }

            var pools = await grounding.FindLocalManyAsync([.. toAsk.Select(i => items[i].Facts)], cancellationToken);
            var local = new Dictionary<int, LocalPool>();
            for (var k = 0; k < toAsk.Count; k++)
            {
                local[toAsk[k]] = pools[k];
            }

            using (ProviderCalls.FailFast()) // a person is waiting
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(options.ResolveBudget);
                using var places = new SemaphoreSlim(Math.Max(1, options.Parallelism));
                var started = Stopwatch.GetTimestamp();
                Task<ProviderAnswers> Ask(ReadFacts facts) => AskOneAsync(facts, places, started, budget.Token, cancellationToken);

                var asked = await Task.WhenAll(toAsk.Select(async i => (Index: i, Answers: await Ask(items[i].Facts))));
                var scored = asked.Select(a => (a.Index, Answer: grounding.Score(items[a.Index].Facts, items[a.Index].Profile, local[a.Index], a.Answers))).ToList();
                var retried = await grounding.RetryTrimmedAsync(
                    [.. scored.Select(s => new TrimmedRetry(items[s.Index].Facts, items[s.Index].Profile, s.Answer, Ask, budget.Token, local[s.Index]))],
                    cancellationToken);
                for (var k = 0; k < scored.Count; k++)
                {
                    answers[scored[k].Index] = retried[k];
                }

                using var passes = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                passes.CancelAfter(SeriesNeighbour.Budget);
                Task<ProviderAnswers> AskAgain(ReadFacts facts) => AskOneAsync(facts, places, Stopwatch.GetTimestamp(), passes.Token, cancellationToken);

                var withSeries = await grounding.RetrySeriesAsync(
                    [.. items.Select((item, i) => new SeriesRetry(item.Facts, item.Profile, item.Box, answers[i], AskAgain, passes.Token))],
                    (releaseIds, token) => SeriesNeighbour.LocalTitlesAsync(db, releaseIds, token),
                    cancellationToken);
                for (var i = 0; i < items.Count; i++)
                {
                    answers[i] = withSeries[i];
                }

                using var kindPass = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                kindPass.CancelAfter(ShotKindRule.Budget);
                Task<ProviderAnswers> AskKind(ReadFacts facts) => AskOneAsync(facts, places, Stopwatch.GetTimestamp(), kindPass.Token, cancellationToken);

                var withKind = await grounding.RetryShotKindAsync(
                    [.. items.Select((item, i) => new ShotKindRetry(item.Facts, item.Profile, answers[i], AskKind, kindPass.Token))],
                    null,
                    (releaseIds, token) => ShotKindRule.LocalKindsAsync(db, releaseIds, token),
                    cancellationToken,
                    localTitles: (releaseIds, token) => SeriesNeighbour.LocalTitlesAsync(db, releaseIds, token));
                for (var i = 0; i < items.Count; i++)
                {
                    answers[i] = withKind[i];
                }
            }

            return [.. answers];
        }

        /// <summary>Asks the providers about one read when a place is free, within the budget; an ask the budget runs out on, or that fails, is a failed answer (Pending), never an exception.</summary>
        private async Task<ProviderAnswers> AskOneAsync(ReadFacts facts, SemaphoreSlim places, long started, CancellationToken budget, CancellationToken cancellationToken)
        {
            try
            {
                await places.WaitAsync(budget);
                try
                {
                    return await grounding.AskProvidersAsync(facts, cancellationToken, budget);
                }
                finally
                {
                    places.Release();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed("budget", "timeout", started);
            }
#pragma warning disable CA1031 // Whatever goes wrong with one item must not fail the picture.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                logger.LogWarning("Looking up a shelf read failed ({Failure}); leaving it pending.", exception.GetType().Name);
                return Failed("resolver", "failed", started);
            }
        }

        private static ProviderAnswers Failed(string provider, string outcome, long started) =>
            new([], [new TraceStep(provider, outcome, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)], true);
    }
}
