using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.Provisional;

/// <summary>
/// Use case: delete a provisional record. An explicit "this isn't anything": the row is removed with its crop
/// (asset row and file), and the session's counters reconcile. The retired candidate row stays skipped, as the record
/// of the segment.
/// </summary>
public static class DeleteProvisional
{
    public sealed record Command(Guid LibraryId, Guid ProvisionalId);

    public sealed class Handler(IAppDbContext db, IPhotoStore photoStore, ILogger<Handler> logger)
    {
        public async Task<Result> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var provisional = await db.ProvisionalRecords
                .FirstOrDefaultAsync(p => p.LibraryId == command.LibraryId && p.Id == command.ProvisionalId, cancellationToken);
            if (provisional is null)
            {
                return Result.Failure(ScanningErrors.ProvisionalNotFound);
            }

            PhotoFile? dropped = null;
            if (provisional.CropAssetId is { } cropId
                && await db.Assets.FirstOrDefaultAsync(a => a.Id == cropId, cancellationToken) is { } crop
                && !await db.Items.IgnoreQueryFilters().AnyAsync(i => i.CustomCoverAssetId == cropId, cancellationToken))
            {
                // A promoted crop the item still uses as its cover is the item's now, not ours to delete.
                dropped = new PhotoFile(crop.Id, crop.BlobPath);
                provisional.CropAssetId = null;
            }

            await ProvisionalLinks.ClearKeptLinksAsync(db, provisional.Id, cancellationToken);
            db.ProvisionalRecords.Remove(provisional);
            await db.SaveChangesAsync(cancellationToken);
            if (dropped is { } gone)
            {
                await CandidatePhotos.DeleteFilesAsync(db, photoStore, logger, [gone], cancellationToken);
            }

            return Result.Success();
        }
    }
}
