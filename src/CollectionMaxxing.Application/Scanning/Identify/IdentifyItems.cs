using System.Diagnostics;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// Use case: the phone posts what it read from one picture (text and fields, never an image) and gets back candidates that are
/// grounded in the catalogue, scored with the profile that fits each item, and already carry "you own this" (docs/02 sections
/// 4 to 7, docs/04 <c>identify</c>). The model never decides a match alone: an item the catalogue and the providers cannot
/// support is NotFound whatever the phone believed, and only the scorer's thresholds make a Matched. What resolves within the
/// time budget is answered at once; the rest stays Pending for a background job and arrives as <c>candidate.updated</c>.
/// Idempotent per <c>snapId</c>: a retry (or a twin) returns the same candidates and creates none.
/// </summary>
public static class IdentifyItems
{
    /// <summary>The job that resolves an item a request ran out of time for. Carries ids only; the read is kept on the candidate.</summary>
    public const string ResolveJobType = "scan.identify_resolve";

    private const string SafetyKey = ":safety";
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The job that resolves an item a request ran out of time for. <c>ProofItemId</c> carries an edition proof's
    /// binding (null for any other read), so the job finishes the proof the request would have: upgrade on a match,
    /// a T2 row otherwise, never an auto-add.
    /// </summary>
    public sealed record ResolveJob(Guid LibraryId, Guid SessionId, Guid CandidateId, Guid UserId, Guid? ProofItemId = null);

    public sealed record Command(Guid SessionId, IdentifyInput Input);

    public sealed class Handler(
        IAppDbContext db,
        IIdGenerator ids,
        ICurrentUser currentUser,
        TimeProvider clock,
        ISessionLock sessionLock,
        IdentifyGrounding grounding,
        CandidateResolver resolver,
        AutoCommit autoCommit,
        ReleaseCovers releaseCovers,
        IJobQueue jobs,
        ScanningOptions options,
        IGameSerialLookup serials,
        ILogger<Handler> logger)
    {
        public async Task<Result<CandidatesDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("IdentifyItems needs a signed-in caller; the endpoint requires authorization.");

            if (IdentifyCleaning.Validate(command.Input) is { } invalid)
            {
                return invalid;
            }

            var session = await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken);
            if (session is null)
            {
                return ScanningErrors.SessionNotFound;
            }

            var input = command.Input;
            if (input.BoundCandidateId is not null)
            {
                return await HandleBoundAsync(command, session, userId, cancellationToken);
            }

            if (input.BoundItemId is not null)
            {
                return await HandleBoundItemAsync(command, session, userId, cancellationToken);
            }

            var snapId = input.SnapId!.Value;
            var items = IdentifyCleaning.Clean(input.Items!, input.Kind, serials, input.PrintKind);
            var now = clock.GetUtcNow();

            // Claim the snap and its rows under the session's lock, so a retry or a twin of this request finds them instead of racing them.
            List<ScanCandidate> candidates;
            List<(ScanCandidate Candidate, CleanItem Item)> fresh = [];
            await using (await sessionLock.AcquireAsync(session.Id, cancellationToken))
            {
                if (await db.ScanSnaps.AnyAsync(s => s.Id == snapId && s.SessionId == session.Id, cancellationToken))
                {
                    return await ReplayAsync(session, snapId, userId, cancellationToken);
                }

                // A snapId is unique everywhere; one taken by another session is refused without saying whose it is.
                if (await db.ScanSnaps.IgnoreQueryFilters().AnyAsync(s => s.Id == snapId, cancellationToken))
                {
                    return IdentifyErrors.SnapInUse;
                }

                if (session.Status != ScanSessionStatus.Open)
                {
                    return ScanningErrors.SessionClosed;
                }

                db.ScanSnaps.Add(new ScanSnap
                {
                    Id = snapId,
                    LibraryId = session.LibraryId,
                    SessionId = session.Id,
                    Trigger = input.Trigger!.Value,
                    ShotKind = input.ShotKind ?? ShotKind.Unknown,
                    Engine = input.Engine!.Value,
                    FellBack = input.FellBackFrom is not null,
                    FallbackReason = input.FellBackFrom,
                    ItemCount = items.Count,
                    AssetId = null,
                    CreatedAt = now,
                });

                var nextSeq = (await db.ScanCandidates.Where(c => c.SessionId == session.Id).MaxAsync(c => (int?)c.Seq, cancellationToken) ?? 0) + 1;
                var source = input.Engine == ScanEngine.AppleModel ? CandidateSource.PictureAppleModel : CandidateSource.PictureText;
                candidates = [];
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var candidate = new ScanCandidate
                    {
                        Id = ids.NewId(),
                        LibraryId = session.LibraryId,
                        SessionId = session.Id,
                        Seq = nextSeq++,
                        Source = source,
                        ShotKind = ShotReading.Of(input.ShotKind, items.Count), // one item read is a cover shot, whatever the framing said
                        SegmentId = item.SegmentId,
                        VisibleText = item.Facts.ShownText,
                        SnapId = snapId,
                        LocalId = item.LocalId,
                        Resolution = CandidateResolution.Pending,
                        Decision = CandidateDecision.Undecided,
                        GuidanceCode = GuidanceCodes.For(input.FellBackFrom, CandidateResolution.Pending, source, null, quotaSpent: false),
                        InferredJson = StoredRead.Serialize(item.Facts, item.Profile, item.Unconfirmed),
                        IdempotencyKey = $"snap:{snapId:N}:{i}",
                        CreatedAt = now,
                    };
                    db.ScanCandidates.Add(candidate);
                    candidates.Add(candidate);
                    fresh.Add((candidate, item));
                }

                await db.SaveChangesAsync(cancellationToken);

                // A safety net queued before the slow lookups, due once this request's budget has long passed: if the request is cut
                // off (a dropped connection, a crash) the rows still get resolved. It is a no-op for anything already resolved.
                await EnqueueResolveJobsAsync(session, candidates, userId, SafetyKey, options.ResolveBudget + SafetyMargin, cancellationToken);
            }

            await ResolveAsync(session, fresh, userId, input.Kind is IdentifyKind.Shelf ? input.PrintKind : null, cancellationToken);
            return new CandidatesDto(await CandidateProjection.ToDtosAsync(db, candidates, cancellationToken));
        }

        /// <summary>
        /// A bound cover (Review's "Snap cover", design §8(b)): the read is stamped onto the bound row, which is then
        /// resolved through the same identify path as a fresh read — scored as a cover, answered with the top three and
        /// their artwork for tap-to-pick. The row keeps its id, copies and decisions; only its read and answer change.
        /// Safe to retry: the same <c>snapId</c> gets the same row back.
        /// </summary>
        private async Task<Result<CandidatesDto>> HandleBoundAsync(
            Command command, ScanSession session, Guid userId, CancellationToken cancellationToken)
        {
            var input = command.Input;
            var candidate = await db.ScanCandidates.FirstOrDefaultAsync(
                c => c.SessionId == session.Id && c.Id == input.BoundCandidateId, cancellationToken);
            if (candidate is null)
            {
                return ScanningErrors.CandidateNotFound;
            }

            var snapId = input.SnapId!.Value;
            var item = IdentifyCleaning.Clean(input.Items!, input.Kind, serials)[0]; // Validate kept exactly one item
            var now = clock.GetUtcNow();

            await using (await sessionLock.AcquireAsync(session.Id, cancellationToken))
            {
                if (await db.ScanSnaps.AnyAsync(s => s.Id == snapId && s.SessionId == session.Id, cancellationToken))
                {
                    return await ReplayAsync(session, snapId, userId, cancellationToken);
                }

                // A snapId is unique everywhere; one taken by another session is refused without saying whose it is.
                if (await db.ScanSnaps.IgnoreQueryFilters().AnyAsync(s => s.Id == snapId, cancellationToken))
                {
                    return IdentifyErrors.SnapInUse;
                }

                if (session.Status != ScanSessionStatus.Open)
                {
                    return ScanningErrors.SessionClosed;
                }

                db.ScanSnaps.Add(new ScanSnap
                {
                    Id = snapId,
                    LibraryId = session.LibraryId,
                    SessionId = session.Id,
                    Trigger = input.Trigger!.Value,
                    ShotKind = input.ShotKind ?? ShotKind.Unknown,
                    Engine = input.Engine!.Value,
                    FellBack = input.FellBackFrom is not null,
                    FallbackReason = input.FellBackFrom,
                    ItemCount = 1,
                    AssetId = null,
                    CreatedAt = now,
                });

                // ShotKind, SegmentId and LocalId are init-only (history, not state), so the bound read is written through EF.
                var entry = db.ScanCandidates.Attach(candidate);
                entry.Property(c => c.ShotKind).CurrentValue = ShotReading.Of(input.ShotKind, 1); // a bound cover is one item
                entry.Property(c => c.SegmentId).CurrentValue = item.SegmentId;
                entry.Property(c => c.LocalId).CurrentValue = item.LocalId;
                entry.Property(c => c.SnapId).CurrentValue = snapId;
                entry.Property(c => c.Resolution).CurrentValue = CandidateResolution.Pending;
                entry.Property(c => c.InferredJson).CurrentValue = StoredRead.Serialize(item.Facts, item.Profile, item.Unconfirmed);
                entry.Property(c => c.GuidanceCode).CurrentValue = GuidanceCodes.For(
                    input.FellBackFrom, CandidateResolution.Pending, candidate.Source, candidate.RawCode, quotaSpent: false);
                if (item.Facts.ShownText is { } text)
                {
                    // Through this same entry, not SetVisibleText: attaching the row a second time would drop the marks above.
                    entry.Property(c => c.VisibleText).CurrentValue = text.Length > 2000 ? text[..2000] : text;
                }

                await db.SaveChangesAsync(cancellationToken);

                // A safety net queued before the slow lookups, due once this request's budget has long passed: if the request is cut
                // off (a dropped connection, a crash) the row still gets resolved. It is a no-op for anything already resolved.
                await EnqueueResolveJobsAsync(session, [candidate], userId, SafetyKey, options.ResolveBudget + SafetyMargin, cancellationToken);
            }

            await ResolveAsync(session, [(candidate, item)], userId, null, cancellationToken);
            return new CandidatesDto(await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken));
        }

        /// <summary>
        /// An edition proof (a book's cover + copyright page, design §11): the read becomes one candidate, which is then
        /// resolved through the same identify path as a fresh read — scored as a cover. A match on the bound item's own
        /// work upgrades its release link in place and verifies it; anything else leaves a T2 row for review and the item
        /// is never touched, and a proof never auto-adds. Safe to retry: the same <c>snapId</c> gets the same row back.
        /// Only an owner or editor may send one (<see cref="EditionProofAccess"/>); a viewer gets a 403 before anything is read.
        /// </summary>
        private async Task<Result<CandidatesDto>> HandleBoundItemAsync(
            Command command, ScanSession session, Guid userId, CancellationToken cancellationToken)
        {
            // Before anything is looked up or written: a proof can rewrite an item, which a viewer may not do, and the answer
            // must not depend on whether the item exists (a viewer could otherwise tell real item ids from made-up ones).
            if (!EditionProofAccess.Allows(currentUser.RoleIn(session.LibraryId)))
            {
                return EditionProofAccess.EditorRequired;
            }

            var input = command.Input;
            var itemId = input.BoundItemId!.Value;
            var item = await db.Items.FirstOrDefaultAsync(
                i => i.LibraryId == session.LibraryId && i.Id == itemId && i.DeletedAt == null, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            var snapId = input.SnapId!.Value;
            var proof = IdentifyCleaning.Clean(input.Items!, input.Kind, serials)[0]; // Validate kept exactly one item
            var now = clock.GetUtcNow();

            ScanCandidate candidate;
            await using (await sessionLock.AcquireAsync(session.Id, cancellationToken))
            {
                if (await db.ScanSnaps.AnyAsync(s => s.Id == snapId && s.SessionId == session.Id, cancellationToken))
                {
                    return await ReplayAsync(session, snapId, userId, cancellationToken);
                }

                // A snapId is unique everywhere; one taken by another session is refused without saying whose it is.
                if (await db.ScanSnaps.IgnoreQueryFilters().AnyAsync(s => s.Id == snapId, cancellationToken))
                {
                    return IdentifyErrors.SnapInUse;
                }

                if (session.Status != ScanSessionStatus.Open)
                {
                    return ScanningErrors.SessionClosed;
                }

                db.ScanSnaps.Add(new ScanSnap
                {
                    Id = snapId,
                    LibraryId = session.LibraryId,
                    SessionId = session.Id,
                    Trigger = input.Trigger!.Value,
                    ShotKind = input.ShotKind ?? ShotKind.Unknown,
                    Engine = input.Engine!.Value,
                    FellBack = input.FellBackFrom is not null,
                    FallbackReason = input.FellBackFrom,
                    ItemCount = 1,
                    AssetId = null,
                    CreatedAt = now,
                });

                var nextSeq = (await db.ScanCandidates.Where(c => c.SessionId == session.Id).MaxAsync(c => (int?)c.Seq, cancellationToken) ?? 0) + 1;
                var source = input.Engine == ScanEngine.AppleModel ? CandidateSource.PictureAppleModel : CandidateSource.PictureText;
                candidate = new ScanCandidate
                {
                    Id = ids.NewId(),
                    LibraryId = session.LibraryId,
                    SessionId = session.Id,
                    Seq = nextSeq,
                    Source = source,
                    ShotKind = ShotReading.Of(input.ShotKind, 1), // an edition proof is one item
                    SegmentId = proof.SegmentId,
                    VisibleText = proof.Facts.ShownText,
                    SnapId = snapId,
                    LocalId = proof.LocalId,
                    Resolution = CandidateResolution.Pending,
                    Decision = CandidateDecision.Undecided,
                    GuidanceCode = GuidanceCodes.For(input.FellBackFrom, CandidateResolution.Pending, source, null, quotaSpent: false),
                    InferredJson = StoredRead.Serialize(proof.Facts, proof.Profile, proof.Unconfirmed),
                    IdempotencyKey = $"snap:{snapId:N}:0",
                    CreatedAt = now,
                };
                db.ScanCandidates.Add(candidate);
                await db.SaveChangesAsync(cancellationToken);

                // A safety net queued before the slow lookups, due once this request's budget has long passed: if the request is cut
                // off (a dropped connection, a crash) the row still gets resolved. It is a no-op for anything already resolved.
                // It carries the proof binding, so the job finishes the proof the request would have.
                await EnqueueResolveJobsAsync(session, [candidate], userId, SafetyKey, options.ResolveBudget + SafetyMargin, cancellationToken, item.Id);
            }

            await ResolveProofAsync(session, candidate, proof, item.Id, userId, cancellationToken);
            return new CandidatesDto(await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken));
        }

        /// <summary>
        /// Resolves an edition proof's read and completes it: upgrade on a match of the item's own work, a T2 row
        /// otherwise. Never auto-adds: a proof verifies an item the library already has, it adds nothing.
        /// </summary>
        private async Task ResolveProofAsync(
            ScanSession session, ScanCandidate candidate, CleanItem proof, Guid itemId, Guid userId, CancellationToken cancellationToken)
        {
            BarcodeResolution answer;
            if (proof.LocalMatchReleaseId is { } claim && await ClaimUsableAsync(session.LibraryId, claim, cancellationToken))
            {
                answer = ClaimedRelease(claim);
            }
            else if (proof.Facts.SearchText is null)
            {
                answer = grounding.Score(proof.Facts, proof.Profile, LocalPool.Empty, ProviderAnswers.None);
            }
            else
            {
                var local = await grounding.FindLocalAsync(proof.Facts, cancellationToken);
                using var failFast = ProviderCalls.FailFast(); // a person is waiting
                using var round = new AskRound(options, cancellationToken);
                answer = (await GroundTextAsync([(candidate, proof)], new Dictionary<Guid, LocalPool> { [candidate.Id] = local }, round, cancellationToken)).Single().Answer;
            }

            // The answer lands under the session's lock: the session is reloaded inside it (another request may
            // have changed it while the providers were being asked).
            await using (await sessionLock.AcquireAsync(session.Id, cancellationToken))
            {
                await db.ScanSessions.Entry(session).ReloadAsync(cancellationToken);
                if (db.ScanSessions.Entry(session).State == EntityState.Detached)
                {
                    return; // cleaned up mid-resolve; the answer dies with the session
                }

                await resolver.ApplyAsync(session.LibraryId, candidate, answer, cancellationToken);
                if (!await ProofUpgrade.TryApplyAsync(db, session, candidate, itemId, currentUser.RoleIn(session.LibraryId), clock, cancellationToken)
                    && candidate.Resolution != CandidateResolution.Pending)
                {
                    candidate.Tier = ScanTier.T2; // a proof that disagrees is reviewed; the item is never touched
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            await EnqueueResolveJobsAsync(session, [candidate], userId, null, null, cancellationToken, itemId);
        }

        /// <summary>The phone's own feature-print hit is a claim: honoured only for a release this library may use.</summary>
        private async Task<bool> ClaimUsableAsync(Guid libraryId, Guid claim, CancellationToken cancellationToken) =>
            await db.Releases.AsNoTracking()
                .AnyAsync(r => r.Id == claim && (r.OwnerLibraryId == null || r.OwnerLibraryId == libraryId), cancellationToken);

        private static BarcodeResolution ClaimedRelease(Guid claim) => new(
            ResolutionVerdict.Matched, [new ResolutionOption(claim, null, 1.0)], null, [new TraceStep("feature_print", "hit", 0, 1.0)]);

        private async Task ResolveAsync(
            ScanSession session, List<(ScanCandidate Candidate, CleanItem Item)> fresh, Guid userId, WorkKind? printKind, CancellationToken cancellationToken)
        {
            // The phone's own feature-print hit is a claim: honoured only for a release this library may use.
            var claimed = fresh.Select(f => f.Item.LocalMatchReleaseId).OfType<Guid>().Distinct().ToList();
            var usable = claimed.Count == 0
                ? []
                : (await db.Releases.AsNoTracking()
                    .Where(r => claimed.Contains(r.Id) && (r.OwnerLibraryId == null || r.OwnerLibraryId == session.LibraryId))
                    .Select(r => r.Id)
                    .ToListAsync(cancellationToken)).ToHashSet();

            var answers = new Dictionary<Guid, BarcodeResolution>();
            var local = new Dictionary<Guid, LocalPool>();
            var toAsk = new List<(ScanCandidate Candidate, CleanItem Item)>();
            foreach (var (candidate, item) in fresh)
            {
                if (item.LocalMatchReleaseId is { } claim && usable.Contains(claim))
                {
                    answers[candidate.Id] = ClaimedRelease(claim);
                }
                else if (item.Facts.SearchText is null)
                {
                    answers[candidate.Id] = grounding.Score(item.Facts, item.Profile, LocalPool.Empty, ProviderAnswers.None);
                }
                else
                {
                    toAsk.Add((candidate, item));
                }
            }

            // The database step for every read that needs it, together.
            var pools = await grounding.FindLocalManyAsync([.. toAsk.Select(a => a.Item.Facts)], cancellationToken);
            for (var i = 0; i < toAsk.Count; i++)
            {
                local[toAsk[i].Candidate.Id] = pools[i];
            }

            // The reads of the picture are asked within one budget. Then the passes over the whole picture: a spine that is only a
            // series' subtitle, asked again with the series its neighbour carries, and a spine that found another kind than the
            // shelf's, asked again as the shelf's kind. Each has a budget of its own (the same one the eval gives it): the pending
            // job cannot make these passes, so what is left of this request's budget is not what they are given.
            using (ProviderCalls.FailFast()) // a person is waiting
            {
                using (var round = new AskRound(options, cancellationToken))
                {
                    foreach (var (candidate, answer) in await GroundTextAsync(toAsk, local, round, cancellationToken))
                    {
                        answers[candidate.Id] = answer;
                    }
                }

                using var seriesRound = new AskRound(options, cancellationToken, SeriesNeighbour.Budget);
                var withSeries = await grounding.RetrySeriesAsync(
                    [.. fresh.Select(f => new SeriesRetry(
                        f.Item.Facts, f.Item.Profile, f.Item.Box, answers[f.Candidate.Id], facts => AskOneAsync(facts, seriesRound, cancellationToken), seriesRound.Budget.Token))],
                    (releaseIds, token) => SeriesNeighbour.LocalTitlesAsync(db, releaseIds, token),
                    cancellationToken);
                for (var i = 0; i < fresh.Count; i++)
                {
                    answers[fresh[i].Candidate.Id] = withSeries[i];
                }

                using var kindRound = new AskRound(options, cancellationToken, ShotKindRule.Budget);
                var withKind = await grounding.RetryShotKindAsync(
                    [.. fresh.Select(f => new ShotKindRetry(
                        f.Item.Facts, f.Item.Profile, answers[f.Candidate.Id], facts => AskOneAsync(facts, kindRound, cancellationToken), kindRound.Budget.Token))],
                    printKind,
                    (releaseIds, token) => ShotKindRule.LocalKindsAsync(db, releaseIds, token),
                    cancellationToken,
                    localTitles: (releaseIds, token) => SeriesNeighbour.LocalTitlesAsync(db, releaseIds, token));
                for (var i = 0; i < fresh.Count; i++)
                {
                    answers[fresh[i].Candidate.Id] = withKind[i];
                }
            }

            // One book in one place, one candidate per picture: a read that repeats a stronger read of the same release in the same
            // place is skipped. The same release read in another place is another item, each kept.
            var freshAnswers = fresh.Select(f => answers[f.Candidate.Id]).ToList();
            var freshBoxes = fresh.Select(f => f.Item.Box).ToList();
            var repeats = RepeatReads.Of(freshAnswers, freshBoxes).ToHashSet();

            // Where the stronger of a cross-engine repeat is the model's own read, the two engines agreeing there is the
            // confirmation Unconfirmed exists to stand in for, so that read is no longer model-only.
            var confirmedByRepeat = RepeatReads.ConfirmedByOtherEngine(
                freshAnswers, [.. fresh.Select(f => f.Item.Unconfirmed)], freshBoxes).ToHashSet();

            // And one item per place, whatever each engine read there: where the picture model's read of a place and the Vision
            // pipeline's name different releases, the read that grounds better is the row and the other is skipped, so the model's
            // right answer is no longer lost to a Vision read of the author's name or a sticker (ReadPlaces). A read
            // already a repeat has no place left to contest.
            var lostPlace = ReadPlaces.Weaker(
                [.. fresh.Select((f, i) => ReadPlaces.Of(answers[f.Candidate.Id], repeats.Contains(i) ? null : f.Item.Box, f.Item.Unconfirmed))]).ToHashSet();

            // And a spine read that only found itself (a number, a banner, a publisher's mark another spine prints, or an author's
            // name answered by a release titled with it) is skipped too (FragmentReads).
            var fragments = await FragmentsOfAsync(fresh, freshAnswers, cancellationToken);

            // And a spine the catalogue found nothing for, after every retry, has nothing to review (UnfoundSpines). It never wins a
            // place, so this runs after the place settle.
            var unfound = UnfoundSpines.Of([.. fresh.Select((f, i) => new UnfoundRead(f.Item.Profile, freshAnswers[i].Verdict))]).ToHashSet();

            // The answers land under the session's lock: the session is reloaded inside it (another request may
            // have auto-added while the providers were being asked), and two auto-adds landing together both append
            // instead of one overwriting the other.
            await using (await sessionLock.AcquireAsync(session.Id, cancellationToken))
            {
                await db.ScanSessions.Entry(session).ReloadAsync(cancellationToken);
                if (db.ScanSessions.Entry(session).State == EntityState.Detached)
                {
                    return; // cleaned up mid-resolve; the answers die with the session
                }

                // What the answers need about the releases they name is read once for the whole picture, not once per answer.
                var lookups = await resolver.LookUpAsync(session.LibraryId, answers.Values, cancellationToken);
                var coverFiles = new List<string>();
                for (var i = 0; i < fresh.Count; i++)
                {
                    var (candidate, item) = fresh[i];
                    if (confirmedByRepeat.Contains(i))
                    {
                        candidate.InferredJson = StoredRead.Serialize(item.Facts, item.Profile, unconfirmed: false);
                    }

                    await resolver.ApplyAsync(session.LibraryId, candidate, answers[candidate.Id], cancellationToken, lookups: lookups);
                    if (repeats.Contains(i) || lostPlace.Contains(i) || fragments.Contains(i) || unfound.Contains(i))
                    {
                        Skip(
                            candidate,
                            repeats.Contains(i) ? RepeatReads.StepName
                            : lostPlace.Contains(i) ? ReadPlaces.StepName
                            : fragments.Contains(i) ? FragmentReads.StepName
                            : UnfoundSpines.StepName);
                        continue; // never auto-added
                    }

                    await autoCommit.TryAddAsync(session, candidate, userId, currentUser.RoleIn(session.LibraryId), cancellationToken, coverFiles, lookups);
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch
                {
                    await releaseCovers.DeleteFilesAsync(coverFiles); // nothing points at the covers just downloaded
                    throw;
                }
            }

            await EnqueueResolveJobsAsync(session, fresh.Select(f => f.Candidate), userId, null, null, cancellationToken);
        }

        /// <summary>The positions of the reads that are fragments (<see cref="FragmentReads"/>); the catalogue is asked only when a kindless spine read is Ambiguous.</summary>
        private async Task<HashSet<int>> FragmentsOfAsync(
            List<(ScanCandidate Candidate, CleanItem Item)> fresh, List<BarcodeResolution> answers, CancellationToken cancellationToken)
        {
            var judged = fresh.Where((f, i) => answers[i].Verdict == ResolutionVerdict.Ambiguous
                && f.Item.Profile == ScoringProfileKind.ShelfSpine && f.Item.Facts.Kind is null).Any();
            if (!judged)
            {
                return [];
            }

            var releases = await FragmentReads.LocalReleasesAsync(db, FragmentReads.ReleaseIdsOf(answers), cancellationToken);
            return FragmentReads.Of(
                [.. fresh.Select((f, i) => FragmentReads.ReadOf(f.Item.Facts, f.Item.Profile, answers[i], id => releases.TryGetValue(id, out var r) ? r : null))]).ToHashSet();
        }

        /// <summary>
        /// The server's own skip for a read that stands behind a stronger one or that has nothing to show: a repeat of the same book
        /// in the same picture (<see cref="RepeatReads"/>), a read that lost its place to the other engine's better-grounded read of it
        /// (<see cref="ReadPlaces"/>), a spine that only found itself (<see cref="FragmentReads"/>), or a shelf spine the catalogue
        /// found nothing for (<see cref="UnfoundSpines"/>), which <paramref name="step"/> says. The row stays, with its answer, so the
        /// phone's line for the read has a card and the person can still un-skip it; see <see cref="CandidateSkip"/>. Saves nothing;
        /// the caller saves.
        /// </summary>
        private static void Skip(ScanCandidate candidate, string step) => CandidateSkip.Apply(candidate, step);

        /// <summary>
        /// Queues the resolve job for each candidate still Pending. Safe to repeat: one job per candidate and key.
        /// <paramref name="proofItemId"/> carries an edition proof's binding to its job (null for any other read).
        /// </summary>
        private async Task EnqueueResolveJobsAsync(
            ScanSession session, IEnumerable<ScanCandidate> candidates, Guid userId, string? keySuffix, TimeSpan? delay,
            CancellationToken cancellationToken, Guid? proofItemId = null)
        {
            DateTimeOffset? runAfter = delay is { } d ? clock.GetUtcNow() + d : null;
            var pending = candidates
                .Where(c => c.Resolution == CandidateResolution.Pending)
                .Select(c => new JobToEnqueue(
                    new ResolveJob(session.LibraryId, session.Id, c.Id, userId, proofItemId),
                    new JobEnqueueOptions(runAfter, $"{ResolveJobType}:{c.Id}{keySuffix}")))
                .ToList();
            if (pending.Count > 0)
            {
                await jobs.EnqueueManyAsync(ResolveJobType, pending, cancellationToken);
            }
        }

        /// <summary>
        /// Grounds several items that have words to look for, within <paramref name="round"/>'s budget and places (the caller's, so
        /// that the asks it goes on to make share them): the providers are asked about all of them
        /// at once, and each answer is scored against the local pool found for it. An item the budget runs out on comes back with a
        /// failed answer, which scores as Pending. A spine that found nothing is asked again without the stray words at its ends, a
        /// cover or spine that found nothing or only a release it is not sure of without a possessive name in front of its title, and
        /// a cover that found nothing without its sequel number (<see cref="IdentifyGrounding.RetryTrimmedAsync"/>), within the same
        /// budget and the same places, round by round for all such reads together, so the retries are batched the way the first asks were. The answers come back with their candidates, in
        /// the order of <paramref name="toAsk"/>. The caller makes the calls inside <c>ProviderCalls.FailFast</c>.
        /// </summary>
        private async Task<List<(ScanCandidate Candidate, BarcodeResolution Answer)>> GroundTextAsync(
            List<(ScanCandidate Candidate, CleanItem Item)> toAsk, Dictionary<Guid, LocalPool> local, AskRound round, CancellationToken cancellationToken)
        {
            if (toAsk.Count == 0)
            {
                return [];
            }

            var asked = await Task.WhenAll(toAsk.Select(async entry => (entry.Candidate, entry.Item, Answers: await AskOneAsync(entry.Item.Facts, round, cancellationToken))));
            var scored = asked.Select(a => (a.Candidate, a.Item, Answer: grounding.Score(a.Item.Facts, a.Item.Profile, local[a.Candidate.Id], a.Answers))).ToList();

            var retried = await grounding.RetryTrimmedAsync(
                [.. scored.Select(s => new TrimmedRetry(
                    s.Item.Facts, s.Item.Profile, s.Answer, facts => AskOneAsync(facts, round, cancellationToken), round.Budget.Token, local[s.Candidate.Id]))],
                cancellationToken);
            return [.. scored.Select((s, i) => (s.Candidate, retried[i]))];
        }

        /// <summary>Asks the providers about one read when a place is free, within the round's budget; an ask the budget runs out on, or that fails, is a failed answer (Pending), never an exception.</summary>
        private async Task<ProviderAnswers> AskOneAsync(ReadFacts facts, AskRound round, CancellationToken cancellationToken)
        {
            try
            {
                await round.Gate.WaitAsync(round.Budget.Token);
                try
                {
                    return await grounding.AskProvidersAsync(facts, cancellationToken, round.Budget.Token);
                }
                finally
                {
                    round.Gate.Release();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed("budget", "timeout", round.Started);
            }
#pragma warning disable CA1031 // Whatever goes wrong with one item must not fail the picture.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                logger.LogWarning("Looking up a scanned item failed ({Failure}); leaving it pending.", exception.GetType().Name);
                return Failed("resolver", "failed", round.Started);
            }
        }

        /// <summary>The time and places the provider asks of one round share: the request's budget (or the one given), counted from when the round starts, and at most <c>Parallelism</c> asks at once.</summary>
        private sealed class AskRound : IDisposable
        {
            public AskRound(ScanningOptions options, CancellationToken cancellationToken, TimeSpan? budget = null)
            {
                Budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Budget.CancelAfter(budget ?? options.ResolveBudget);
                Gate = new SemaphoreSlim(Math.Max(1, options.Parallelism));
                Started = Stopwatch.GetTimestamp();
            }

            public CancellationTokenSource Budget { get; }

            public SemaphoreSlim Gate { get; }

            public long Started { get; }

            public void Dispose()
            {
                Gate.Dispose();
                Budget.Dispose();
            }
        }

        private static ProviderAnswers Failed(string provider, string outcome, long started) =>
            new([], [new TraceStep(provider, outcome, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)], true);

        private async Task<Result<CandidatesDto>> ReplayAsync(ScanSession session, Guid snapId, Guid userId, CancellationToken cancellationToken)
        {
            var existing = await db.ScanCandidates.AsNoTracking()
                .Where(c => c.SessionId == session.Id && c.SnapId == snapId)
                .OrderBy(c => c.Seq)
                .ToListAsync(cancellationToken);

            // A first attempt that was cut off may have left rows with no job; queueing again is safe (deduped per candidate).
            await EnqueueResolveJobsAsync(session, existing, userId, null, null, cancellationToken);
            return new CandidatesDto(await CandidateProjection.ToDtosAsync(db, existing, cancellationToken));
        }
    }
}
