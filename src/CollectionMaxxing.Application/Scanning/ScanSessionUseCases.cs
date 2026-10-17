using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning.Photo;
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
    public sealed record Command(Guid LibraryId, Guid? DefaultLocationId, ItemStatus? DefaultStatus);

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

            var session = new ScanSession
            {
                Id = ids.NewId(),
                LibraryId = command.LibraryId,
                UserId = userId,
                Status = ScanSessionStatus.Open,
                DefaultLocationId = command.DefaultLocationId,
                DefaultStatus = command.DefaultStatus,
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

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser)
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

            var since = query.Since ?? 0;
            var candidates = await db.ScanCandidates.AsNoTracking()
                .Where(c => c.SessionId == session.Id && c.ChangeSeq > since)
                .OrderBy(c => c.Seq)
                .ToListAsync(cancellationToken);

            return new ScanSessionDto(
                session.Id,
                session.LibraryId,
                session.Status,
                session.DefaultLocationId,
                session.DefaultStatus,
                session.CreatedAt,
                session.CommittedAt,
                await CandidateProjection.ToDtosAsync(db, candidates, cancellationToken));
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
