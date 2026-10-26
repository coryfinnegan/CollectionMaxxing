using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Application.Scanning.Quota;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// Use case: start a scan session in a library. There are no modes: the defaults only save typing at Review. Any member may
/// start one (a viewer scans to check what the library has); committing it needs an editor.
/// </summary>
public static class CreateScanSession
{
    public sealed record Command(
        Guid LibraryId,
        Guid? DefaultLocationId,
        ItemStatus? DefaultStatus,
        ScanMediaHint? MediaHint = null,
        ScanChannel? Channel = null,
        IReadOnlyList<ScanTier>? AutoCommitTiers = null,
        bool AutoCommitCopies = false);

    public sealed class Handler(IAppDbContext db, IIdGenerator ids, ICurrentUser currentUser, TimeProvider clock)
    {
        public async Task<Result<CreatedScanSessionDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("CreateScanSession needs a signed-in caller; the endpoint requires authorization.");

            if (!await db.Libraries.AnyAsync(l => l.Id == command.LibraryId, cancellationToken))
            {
                return Errors.LibraryNotFound;
            }

            if (command.DefaultLocationId is { } locationId
                && !await ItemProjection.LocationInLibraryAsync(db, command.LibraryId, locationId, cancellationToken))
            {
                return ItemErrors.LocationNotFound;
            }

            // T2 is reviewed, never auto-added: naming it is a client bug, refused rather than silently ignored.
            if (command.AutoCommitTiers is { } tiers && tiers.Any(t => t is not (ScanTier.T0 or ScanTier.T1)))
            {
                return ScanningErrors.Invalid("scan.autocommit_tiers_invalid", "Auto-commit tiers can only be T0 and T1.", "autoCommitTiers");
            }

            var session = new ScanSession
            {
                Id = ids.NewId(),
                LibraryId = command.LibraryId,
                UserId = userId,
                Status = ScanSessionStatus.Open,
                DefaultLocationId = command.DefaultLocationId,
                DefaultStatus = command.DefaultStatus,
                MediaHint = command.MediaHint ?? ScanMediaHint.Mixed,
                Channel = command.Channel ?? ScanChannel.Home,
                AutoCommitTiers = command.AutoCommitTiers?.Distinct().Select(EnumText.ToText).ToArray() ?? [EnumText.ToText(ScanTier.T0)],
                AutoCommitCopies = command.AutoCommitCopies,
                CreatedAt = clock.GetUtcNow(),
            };
            db.ScanSessions.Add(session);
            await db.SaveChangesAsync(cancellationToken);
            return new CreatedScanSessionDto(session.Id);
        }
    }
}

/// <summary>
/// Use case: the session and its candidates, in scan order. With <c>since</c>, only those added or changed after that change
/// counter (a candidate's <c>changeSeq</c>, also the id of its events): the polling fallback for the event stream.
/// </summary>
public static class GetScanSession
{
    public sealed record Query(Guid SessionId, int? Since);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, IJobQueue jobs, TimeProvider clock)
    {
        public async Task<Result<ScanSessionDto>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            if (query.Since is < 0)
            {
                return ScanningErrors.Invalid("scan.since_invalid", "since is a change counter, 0 or more.", "since");
            }

            if (await ScanSessions.FindMineAsync(db, currentUser, query.SessionId, cancellationToken) is not { } session)
            {
                return ScanningErrors.SessionNotFound;
            }

            await DrainQuotaBacklogForSessionAsync(session, cancellationToken);

            var since = query.Since ?? 0;
            var candidates = await db.ScanCandidates.AsNoTracking()
                .Where(c => c.SessionId == session.Id)
                .OrderBy(c => c.Seq)
                .ToListAsync(cancellationToken);
            var snaps = await db.ScanSnaps.AsNoTracking()
                .Where(s => s.SessionId == session.Id)
                .ToListAsync(cancellationToken);

            return new ScanSessionDto(
                session.Id,
                session.LibraryId,
                session.Status,
                session.DefaultLocationId,
                session.DefaultStatus,
                session.MediaHint,
                session.Channel,
                session.CreatedAt,
                session.CommittedAt,
                await CandidateProjection.ToDtosAsync(db, candidates.Where(c => c.ChangeSeq > since).ToList(), cancellationToken),
                SessionCounters.Count(candidates),
                SessionCounters.Segments(snaps, candidates),
                session.AutoCommitTiers.Select(EnumText.Parse<ScanTier>).ToList(),
                session.AutoCommitCopies);
        }

        /// <summary>
        /// The session-reopen drain (design §12): reopening a session re-queues its quota-pending codes whose
        /// retry time has passed, so they resolve without waiting for the nightly drain. One job per code per day,
        /// however often the session is opened; a code whose retry time hasn't come is left for the nightly drain.
        /// </summary>
        private async Task DrainQuotaBacklogForSessionAsync(ScanSession session, CancellationToken cancellationToken)
        {
            if (session.Status != ScanSessionStatus.Open)
            {
                return;
            }

            var now = clock.GetUtcNow();
            var waiting = await db.ScanCandidates.AsNoTracking()
                .Where(c => c.SessionId == session.Id && c.Resolution == CandidateResolution.Pending)
                .Select(c => new { c.Id, c.TraceJson })
                .ToListAsync(cancellationToken);
            foreach (var candidate in waiting)
            {
                var trace = CandidateJson.ReadTrace(candidate.TraceJson);
                if (!UpcQuotaWindow.IsSpent(trace.Steps))
                {
                    continue;
                }

                if (trace.RetryAfter is { } retryAfter && retryAfter > now)
                {
                    continue;
                }

                await jobs.EnqueueAsync(
                    SubmitBarcodes.ResolveJobType,
                    new SubmitBarcodes.ResolveJob(session.LibraryId, session.Id, candidate.Id, session.UserId),
                    new JobEnqueueOptions(null, UpcQuotaWindow.DedupeKey(candidate.Id, now)),
                    cancellationToken);
            }
        }
    }
}

/// <summary>
/// Use case: give up on a session. Nothing is added; an abandoned session can't be scanned into or committed. Photos uploaded for its
/// candidates ("Use my photo") are deleted with it: there is no item to be their cover.
/// </summary>
public static class AbandonScanSession
{
    public sealed record Command(Guid SessionId);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, IPhotoStore photoStore, ILogger<Handler> logger)
    {
        public async Task<Result> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken) is not { } session)
            {
                return Result.Failure(ScanningErrors.SessionNotFound);
            }

            if (session.Status == ScanSessionStatus.Committed)
            {
                return Result.Failure(ScanningErrors.AlreadyCommitted);
            }

            if (session.Status == ScanSessionStatus.Open)
            {
                session.Status = ScanSessionStatus.Abandoned;
                var candidates = await db.ScanCandidates.Where(c => c.SessionId == session.Id && c.SnapId != null).ToListAsync(cancellationToken);
                var photos = await CandidatePhotos.DiscardAsync(db, candidates, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await CandidatePhotos.DeleteFilesAsync(db, photoStore, logger, photos, cancellationToken);
            }

            return Result.Success(); // abandoning twice is fine
        }
    }
}
