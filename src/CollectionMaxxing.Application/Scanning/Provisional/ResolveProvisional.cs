using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.Provisional;

/// <summary>
/// Use case: resolve a provisional record to an item the library already has (design §11). The row it came from — when its
/// session still exists — is linked to that item's release as an owned hit, the crop is deleted (the item has its own cover),
/// and the record resolves, staying as the audit trail. Resolving again to the same item answers the stored record.
/// </summary>
public static class ResolveProvisional
{
    public sealed record Command(Guid LibraryId, Guid ProvisionalId, Guid? ItemId);

    public sealed class Handler(
        IAppDbContext db,
        TimeProvider clock,
        CandidateResolver resolver,
        MatchFeedbackRecorder feedback,
        IPhotoStore photoStore,
        ILogger<Handler> logger)
    {
        public async Task<Result<ProvisionalDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (command.ItemId is not { } itemId || itemId == Guid.Empty)
            {
                return ScanningErrors.Invalid("scan.resolve_item_required", "Say which item this kept row is.", "itemId");
            }

            var provisional = await db.ProvisionalRecords
                .FirstOrDefaultAsync(p => p.LibraryId == command.LibraryId && p.Id == command.ProvisionalId, cancellationToken);
            if (provisional is null)
            {
                return ScanningErrors.ProvisionalNotFound;
            }

            var item = await db.Items.AsNoTracking()
                .FirstOrDefaultAsync(i => i.LibraryId == command.LibraryId && i.Id == command.ItemId && i.DeletedAt == null, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            if (provisional.ResolvedAt is not null)
            {
                return provisional.ResolveAction == ProvisionalResolveAction.ResolvedToExisting && provisional.CreatedItemId == item.Id
                    ? ProvisionalProjection.ToDto(provisional) // resolving again to the same item: the stored answer
                    : new DomainError(ErrorKind.Conflict, "scan.provisional_resolved", "That record was already acted on.", "provisionalId");
            }

            PhotoFile? dropped = null;
            if (provisional.CropAssetId is { } cropId)
            {
                var crop = await db.Assets.FirstOrDefaultAsync(a => a.Id == cropId, cancellationToken);
                if (crop is not null)
                {
                    dropped = new PhotoFile(crop.Id, crop.BlobPath);
                }

                provisional.CropAssetId = null;
            }

            provisional.ResolvedAt = clock.GetUtcNow();
            provisional.ResolveAction = ProvisionalResolveAction.ResolvedToExisting;
            provisional.CreatedItemId = item.Id;

            if (provisional.CandidateId is { } candidateId
                && await db.ScanCandidates.FirstOrDefaultAsync(
                    c => c.LibraryId == command.LibraryId && c.Id == candidateId, cancellationToken) is { } candidate)
            {
                var owned = await resolver.OwnedAsync(command.LibraryId, new EnsuredRelease(item.ReleaseId, false), cancellationToken);
                var alternatives = CandidateJson.ReadAlternatives(candidate.AlternativesJson);
                var rankedFirst = alternatives.Count > 0 ? alternatives[0].ReleaseId : candidate.MatchedReleaseId;
                if (rankedFirst is null)
                {
                    await feedback.RecordConfirmedAsync(candidate, item.ReleaseId, cancellationToken);
                }
                else
                {
                    await feedback.RecordPickAsync(candidate, rankedFirst, item.ReleaseId, cancellationToken);
                }
                var trace = CandidateJson.ReadTrace(candidate.TraceJson);
                candidate.MatchedReleaseId = item.ReleaseId;
                candidate.Resolution = CandidateResolution.Matched;
                candidate.GuidanceCode = null; // linked: a resolved row carries no guidance
                candidate.AlreadyOwnedCount = owned.Count;
                candidate.TraceJson = CandidateJson.Serialize(trace with { OwnedAt = CandidateResolver.OwnedAt(owned) });
            }

            await db.SaveChangesAsync(cancellationToken);
            if (dropped is { } gone)
            {
                await CandidatePhotos.DeleteFilesAsync(db, photoStore, logger, [gone], cancellationToken);
            }

            return ProvisionalProjection.ToDto(provisional);
        }
    }
}
