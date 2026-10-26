using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.Provisional;

/// <summary>
/// Use case: promote a provisional record to a real item (design §11). The guessed title becomes a library-private release
/// through the item-creation path, at the record's location; the crop is kept as the item's cover unless the caller says
/// otherwise. The record resolves (staying as the audit trail), and the row it came from — when its session still exists —
/// points at the new item, so the session's counters reconcile. Safe to retry: the same <c>Idempotency-Key</c> returns the
/// same answer, through the item creation's own key.
/// </summary>
public static class PromoteProvisional
{
    public sealed record Command(Guid LibraryId, Guid ProvisionalId, string? IdempotencyKey, bool KeepCrop = true);

    public sealed class Handler(
        IAppDbContext db,
        TimeProvider clock,
        CreateItem.Handler items,
        MatchFeedbackRecorder feedback,
        IPhotoStore photoStore,
        ILogger<Handler> logger)
    {
        public async Task<Result<ProvisionalDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var key = command.IdempotencyKey?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > CreateItem.MaxKeyLength)
            {
                return ItemErrors.Invalid("idempotency.key_required", "Send an Idempotency-Key header of 1 to 200 characters.", "Idempotency-Key");
            }

            var provisional = await db.ProvisionalRecords
                .FirstOrDefaultAsync(p => p.LibraryId == command.LibraryId && p.Id == command.ProvisionalId, cancellationToken);
            if (provisional is null)
            {
                return ScanningErrors.ProvisionalNotFound;
            }

            if (provisional.ResolvedAt is not null)
            {
                return provisional.ResolveAction == ProvisionalResolveAction.Promoted
                    ? ProvisionalProjection.ToDto(provisional) // a retry: the stored answer
                    : new DomainError(ErrorKind.Conflict, "scan.provisional_resolved", "That record was already acted on.", "provisionalId");
            }

            var created = await items.HandleAsync(
                new CreateItem.Command(
                    command.LibraryId,
                    $"provisional-promote:{provisional.Id}:{key}",
                    ReleaseId: null,
                    new ManualRelease(KindOf(provisional.MediaHint), provisional.TitleGuess, null, null, null, null, null),
                    Status: null,
                    Condition: null,
                    Completeness: null,
                    provisional.LocationId,
                    Notes: null,
                    AcquiredOn: null,
                    AddedVia.Picture,
                    TagIds: null),
                cancellationToken);
            if (!created.IsSuccess)
            {
                return created.Error!;
            }

            var item = await db.Items.FirstOrDefaultAsync(
                i => i.LibraryId == command.LibraryId && i.Id == created.Value.Value.Id, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            PhotoFile? dropped = null;
            if (command.KeepCrop && provisional.CropAssetId is { } kept)
            {
                item.CoverChoice = CoverChoice.MyPhoto;
                item.CustomCoverAssetId = kept;
            }
            else if (provisional.CropAssetId is { } cropId)
            {
                var crop = await db.Assets.FirstOrDefaultAsync(a => a.Id == cropId, cancellationToken);
                if (crop is not null)
                {
                    dropped = new PhotoFile(crop.Id, crop.BlobPath);
                }

                provisional.CropAssetId = null;
            }

            provisional.ResolvedAt = clock.GetUtcNow();
            provisional.ResolveAction = ProvisionalResolveAction.Promoted;
            provisional.CreatedItemId = item.Id;

            if (provisional.CandidateId is { } candidateId
                && await db.ScanCandidates.FirstOrDefaultAsync(
                    c => c.LibraryId == command.LibraryId && c.Id == candidateId, cancellationToken) is { } candidate)
            {
                candidate.CreatedItemId = item.Id;
                await feedback.RecordConfirmedAsync(candidate, item.ReleaseId, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            if (dropped is { } gone)
            {
                await CandidatePhotos.DeleteFilesAsync(db, photoStore, logger, [gone], cancellationToken);
            }

            return ProvisionalProjection.ToDto(provisional);
        }

        private static WorkKind? KindOf(ScanMediaHint hint) => hint switch
        {
            ScanMediaHint.Books => WorkKind.Book,
            ScanMediaHint.Games => WorkKind.Game,
            ScanMediaHint.Movies => WorkKind.Film,
            _ => null,
        };
    }
}
