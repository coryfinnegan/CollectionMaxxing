using System.Diagnostics;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.ShelfReading;

/// <summary>What a shelf read answers: the rows it added, and how many items the reader read (for the log; never their words).</summary>
public sealed record ShelfReadAnswer(CandidatesDto Candidates, int ItemsRead);

/// <summary>
/// Use case: the phone has had <c>identify</c> answer a shelf snap, and now sends the same picture for the server's own reader (ADR-033).
/// The picture is read once, in memory: it is never stored and nothing of it outlives the request. What the reader finds is grounded
/// as <c>identify</c> grounds a read, and <see cref="PcShelfUpgrade"/> decides which finds are added beside the rows the snap already
/// has. Added rows are unconfirmed (<see cref="CandidateSource.PictureServerModel"/>): a person ticks them in Review, nothing adds
/// them to the library by itself, and no existing row is ever changed. A snap is read once; sending it again answers the same rows.
/// </summary>
public static class ReadShelfSnap
{
    public sealed record Command(Guid SessionId, Guid SnapId, byte[] Jpeg, IReadOnlyList<string>? Lines);

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
        /// <summary>The most lines of the shelf's print that are used.</summary>
        public const int MaxLines = 1000;

        /// <summary>The longest a line of print may be; the rest is dropped.</summary>
        public const int MaxLineLength = 500;

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

            if (read.Status is ShelfReadStatus.TimedOut or ShelfReadStatus.Unavailable)
            {
                return ShelfReadErrors.ReaderUnavailable; // nothing was changed; the snap can be sent again
            }

            return await KeepAsync(session, snap, read.Items, command.Lines, userId, cancellationToken);
        }

        /// <summary>Grounds what the reader found, lets <see cref="PcShelfUpgrade"/> pick what stands beside the snap's rows, and keeps those as unconfirmed rows.</summary>
        private async Task<Result<ShelfReadAnswer>> KeepAsync(
            ScanSession session, ScanSnap snap, IReadOnlyList<ShelfReadItem> found, IReadOnlyList<string>? lines, Guid userId, CancellationToken cancellationToken)
        {
            var reads = found.Where(item => !string.IsNullOrWhiteSpace(item.Title)).Take(MaxItems).Select(ServerReadInput.ReadOf).ToList();
            var items = IdentifyCleaning.Clean(reads, IdentifyKind.Shelf, serials);
            var answers = await GroundAsync(items, cancellationToken);

            // An unfound spine has nothing to review (UnfoundSpines), as in the eval row.
            var unfound = UnfoundSpines.Of([.. items.Select((item, i) => new UnfoundRead(item.Profile, answers[i].Verdict))]).ToHashSet();
            var made = new List<(ScanCandidate Candidate, string? Title, PcShelfUpgrade.ReleaseKey? Key)>();
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
                    InferredJson = StoredRead.Serialize(item.Facts, item.Profile, unconfirmed: true), // only the server's model saw it
                    IdempotencyKey = $"snap:{snap.Id:N}:server:{i}",
                    CreatedAt = clock.GetUtcNow(),
                };

                // Written onto the row as identify writes an answer, so the best release is what the row will show.
                await resolver.ApplyAsync(session.LibraryId, candidate, answers[i], cancellationToken);
                made.Add((candidate, item.Facts.Title, BestReleaseOf(candidate.AlternativesJson)));
            }

            var printed = PcShelfUpgrade.PrintOf(lines?.Take(MaxLines).Select(line => line.Length > MaxLineLength ? line[..MaxLineLength] : line));
            var added = new List<ScanCandidate>();
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

                // The rows the snap has now, the phone's reads included (skipped ones too: a skipped row still names its release).
                var existing = (await db.ScanCandidates.AsNoTracking()
                        .Where(c => c.SessionId == session.Id && c.SnapId == snap.Id && c.Source != CandidateSource.PictureServerModel)
                        .Select(c => c.AlternativesJson)
                        .ToListAsync(cancellationToken))
                    .Select(BestReleaseOf)
                    .ToList();

                var nextSeq = (await db.ScanCandidates.Where(c => c.SessionId == session.Id).MaxAsync(c => (int?)c.Seq, cancellationToken) ?? 0) + 1;
                foreach (var (candidate, title, key) in made)
                {
                    if (!PcShelfUpgrade.Stands(key, title, existing, printed))
                    {
                        continue;
                    }

                    db.ScanCandidates.Add(candidate);
                    db.ScanCandidates.Entry(candidate).Property(c => c.Seq).CurrentValue = nextSeq++; // Seq is history, so it is written through EF
                    added.Add(candidate);
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

            return new ShelfReadAnswer(new CandidatesDto(await CandidateProjection.ToDtosAsync(db, added, cancellationToken)), found.Count);
        }

        /// <summary>The release a row shows first (its best alternative), as the key the upgrade rule compares; null when it shows none.</summary>
        private static PcShelfUpgrade.ReleaseKey? BestReleaseOf(string? alternativesJson) =>
            CandidateJson.ReadAlternatives(alternativesJson) is { Count: > 0 } alternatives
                ? PcShelfUpgrade.ReleaseKey.Of(alternatives[0].Title, alternatives[0].Year)
                : null;

        /// <summary>The rows an earlier read of this snap added, in order: what a second request for the same snap answers.</summary>
        private async Task<Result<ShelfReadAnswer>> RowsOfAsync(ScanSession session, ScanSnap snap, CancellationToken cancellationToken)
        {
            var rows = await db.ScanCandidates.AsNoTracking()
                .Where(c => c.SessionId == session.Id && c.SnapId == snap.Id && c.Source == CandidateSource.PictureServerModel)
                .OrderBy(c => c.Seq)
                .ToListAsync(cancellationToken);
            return new ShelfReadAnswer(new CandidatesDto(await CandidateProjection.ToDtosAsync(db, rows, cancellationToken)), 0);
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
