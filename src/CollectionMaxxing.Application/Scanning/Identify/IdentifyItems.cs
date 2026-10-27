using System.Diagnostics;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Jobs;
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
            var items = IdentifyCleaning.Clean(input.Items!, input.Kind);
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
                        ShotKind = input.ShotKind ?? ShotKind.Unknown,
                        SegmentId = item.SegmentId,
                        VisibleText = item.Facts.Text ?? item.Facts.Title,
                        SnapId = snapId,
                        LocalId = item.LocalId,
                        Resolution = CandidateResolution.Pending,
                        Decision = CandidateDecision.Undecided,
                        GuidanceCode = GuidanceCodes.For(input.FellBackFrom, CandidateResolution.Pending, source, null, quotaSpent: false),
                        InferredJson = StoredRead.Serialize(item.Facts, item.Profile),
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

            await ResolveAsync(session, fresh, userId, cancellationToken);
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
            var item = IdentifyCleaning.Clean(input.Items!, input.Kind)[0]; // Validate kept exactly one item
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
                entry.Property(c => c.ShotKind).CurrentValue = input.ShotKind ?? ShotKind.Unknown;
                entry.Property(c => c.SegmentId).CurrentValue = item.SegmentId;
                entry.Property(c => c.LocalId).CurrentValue = item.LocalId;
                entry.Property(c => c.SnapId).CurrentValue = snapId;
                entry.Property(c => c.Resolution).CurrentValue = CandidateResolution.Pending;
                entry.Property(c => c.InferredJson).CurrentValue = StoredRead.Serialize(item.Facts, item.Profile);
                entry.Property(c => c.GuidanceCode).CurrentValue = GuidanceCodes.For(
                    input.FellBackFrom, CandidateResolution.Pending, candidate.Source, candidate.RawCode, quotaSpent: false);
                if ((item.Facts.Text ?? item.Facts.Title) is { } text)
                {
                    // Through this same entry, not SetVisibleText: attaching the row a second time would drop the marks above.
                    entry.Property(c => c.VisibleText).CurrentValue = text.Length > 2000 ? text[..2000] : text;
                }

                await db.SaveChangesAsync(cancellationToken);

                // A safety net queued before the slow lookups, due once this request's budget has long passed: if the request is cut
                // off (a dropped connection, a crash) the row still gets resolved. It is a no-op for anything already resolved.
                await EnqueueResolveJobsAsync(session, [candidate], userId, SafetyKey, options.ResolveBudget + SafetyMargin, cancellationToken);
            }

            await ResolveAsync(session, [(candidate, item)], userId, cancellationToken);
            return new CandidatesDto(await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken));
        }

        /// <summary>
        /// An edition proof (a book's cover + copyright page, design §11): the read becomes one candidate, which is then
        /// resolved through the same identify path as a fresh read — scored as a cover. A match on the bound item's own
        /// work upgrades its release link in place and verifies it; anything else leaves a T2 row for review and the item
        /// is never touched, and a proof never auto-adds. Safe to retry: the same <c>snapId</c> gets the same row back.
        /// </summary>
        private async Task<Result<CandidatesDto>> HandleBoundItemAsync(
            Command command, ScanSession session, Guid userId, CancellationToken cancellationToken)
        {
            var input = command.Input;
            var itemId = input.BoundItemId!.Value;
            var item = await db.Items.FirstOrDefaultAsync(
                i => i.LibraryId == session.LibraryId && i.Id == itemId && i.DeletedAt == null, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            var snapId = input.SnapId!.Value;
            var proof = IdentifyCleaning.Clean(input.Items!, input.Kind)[0]; // Validate kept exactly one item
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
                    ShotKind = input.ShotKind ?? ShotKind.Unknown,
                    SegmentId = proof.SegmentId,
                    VisibleText = proof.Facts.Text ?? proof.Facts.Title,
                    SnapId = snapId,
                    LocalId = proof.LocalId,
                    Resolution = CandidateResolution.Pending,
                    Decision = CandidateDecision.Undecided,
                    GuidanceCode = GuidanceCodes.For(input.FellBackFrom, CandidateResolution.Pending, source, null, quotaSpent: false),
                    InferredJson = StoredRead.Serialize(proof.Facts, proof.Profile),
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
                var (_, _, providers) = (await AskProvidersAsync([(candidate, proof)], cancellationToken)).Single();
                answer = grounding.Score(proof.Facts, proof.Profile, local, providers);
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
                if (!await ProofUpgrade.TryApplyAsync(db, session, candidate, itemId, clock, cancellationToken)
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
            ScanSession session, List<(ScanCandidate Candidate, CleanItem Item)> fresh, Guid userId, CancellationToken cancellationToken)
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
                    local[candidate.Id] = await grounding.FindLocalAsync(item.Facts, cancellationToken);
                    toAsk.Add((candidate, item));
                }
            }

            foreach (var (candidate, item, providers) in await AskProvidersAsync(toAsk, cancellationToken))
            {
                answers[candidate.Id] = grounding.Score(item.Facts, item.Profile, local[candidate.Id], providers);
            }

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

                var coverFiles = new List<string>();
                foreach (var (candidate, _) in fresh)
                {
                    await resolver.ApplyAsync(session.LibraryId, candidate, answers[candidate.Id], cancellationToken);
                    await autoCommit.TryAddAsync(session, candidate, userId, currentUser.RoleIn(session.LibraryId), cancellationToken, coverFiles);
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

        /// <summary>
        /// Queues the resolve job for each candidate still Pending. Safe to repeat: one job per candidate and key.
        /// <paramref name="proofItemId"/> carries an edition proof's binding to its job (null for any other read).
        /// </summary>
        private async Task EnqueueResolveJobsAsync(
            ScanSession session, IEnumerable<ScanCandidate> candidates, Guid userId, string? keySuffix, TimeSpan? delay,
            CancellationToken cancellationToken, Guid? proofItemId = null)
        {
            DateTimeOffset? runAfter = delay is { } d ? clock.GetUtcNow() + d : null;
            foreach (var candidate in candidates.Where(c => c.Resolution == CandidateResolution.Pending))
            {
                await jobs.EnqueueAsync(
                    ResolveJobType,
                    new ResolveJob(session.LibraryId, session.Id, candidate.Id, userId, proofItemId),
                    new JobEnqueueOptions(runAfter, $"{ResolveJobType}:{candidate.Id}{keySuffix}"),
                    cancellationToken);
            }
        }

        /// <summary>Asks the providers about several items at once within the request's budget. An item the budget runs out on comes back with a failed answer, which scores as Pending.</summary>
        private async Task<List<(ScanCandidate Candidate, CleanItem Item, ProviderAnswers Answers)>> AskProvidersAsync(
            List<(ScanCandidate Candidate, CleanItem Item)> toAsk, CancellationToken cancellationToken)
        {
            if (toAsk.Count == 0)
            {
                return [];
            }

            using var failFast = CollectionMaxxing.Application.Catalog.Providers.ProviderCalls.FailFast(); // a person is waiting
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ResolveBudget);
            using var gate = new SemaphoreSlim(Math.Max(1, options.Parallelism));
            var started = Stopwatch.GetTimestamp();

            var tasks = toAsk.Select(async entry =>
            {
                try
                {
                    await gate.WaitAsync(timeout.Token);
                    try
                    {
                        return (entry.Candidate, entry.Item, Answers: await grounding.AskProvidersAsync(entry.Item.Facts, cancellationToken, timeout.Token));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return (entry.Candidate, entry.Item, Answers: Failed("budget", "timeout", started));
                }
#pragma warning disable CA1031 // Whatever goes wrong with one item must not fail the picture.
                catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    logger.LogWarning("Looking up a scanned item failed ({Failure}); leaving it pending.", exception.GetType().Name);
                    return (entry.Candidate, entry.Item, Answers: Failed("resolver", "failed", started));
                }
            }).ToList();

            return [.. await Task.WhenAll(tasks)];
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
