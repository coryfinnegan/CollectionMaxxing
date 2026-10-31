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
/// Use case: promote a provisional record to a real item (design §11). The item is made from the kept row's resolved
/// release when it has one (a barcode scanned after the keep), else the guessed title becomes a library-private release
/// through the item-creation path, at the record's location; the crop is kept as the item's cover unless the caller says
/// otherwise. The record resolves (staying as the audit trail), and the row it came from — when its session still exists —
/// points at the new item, so the session's counters reconcile. Safe to retry, and safe to repeat: a kept row becomes one
/// item however many times, or from however many tabs, it is promoted. Once it has resolved, any repeat (with any
/// <c>Idempotency-Key</c>) returns the stored answer; promotes racing before that share one item-creation key.
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

            // A kept row that resolved afterwards (its barcode was scanned, design §11) promotes to that release —
            // verified — instead of a manual record typed from the guess.
            //
            // The item's own idempotency key names the kept row and nothing else, never the caller's header: two promotes of the
            // same row (a double tap, two tabs) then collide on one key, so only one item is made. The one that loses either
            // replays the winner's item or is refused whole by the database (a 409), and in neither case leaves an item behind.
            var releaseId = await MatchedReleaseAsync(command.LibraryId, provisional.CandidateId, cancellationToken);

            // The item is dated the day the person was in when they scanned it; a session that was cleaned up has no offset left to ask.
            var utcOffsetMinutes = provisional.SessionId is { } sessionId
                ? await db.ScanSessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.UtcOffsetMinutes).FirstOrDefaultAsync(cancellationToken)
                : null;
            var created = await items.HandleAsync(
                new CreateItem.Command(
                    command.LibraryId,
                    $"provisional-promote:{provisional.Id}",
                    ReleaseId: releaseId,
                    releaseId is null
                        ? new ManualRelease(KindOf(provisional.MediaHint), provisional.TitleGuess, null, null, null, null, null)
                        : null,
                    Status: null,
                    Condition: null,
                    Completeness: null,
                    provisional.LocationId,
                    Notes: null,
                    AcquiredOn: null,
                    AddedVia.Picture,
                    TagIds: null,
                    UtcOffsetMinutes: utcOffsetMinutes),
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

        /// <summary>
        /// The kept row's match, when it has one this library may use. A row that never resolved, a match on another
        /// library's private release, or a row whose session is gone all fall back to the manual guess.
        /// </summary>
        private async Task<Guid?> MatchedReleaseAsync(Guid libraryId, Guid? candidateId, CancellationToken cancellationToken)
        {
            if (candidateId is null)
            {
                return null;
            }

            var matched = await db.ScanCandidates.AsNoTracking()
                .Where(c => c.LibraryId == libraryId && c.Id == candidateId)
                .Select(c => c.MatchedReleaseId)
                .SingleOrDefaultAsync(cancellationToken);
            if (matched is not { } releaseId)
            {
                return null;
            }

            return await ItemProjection.CanUseReleaseAsync(db, libraryId, releaseId, cancellationToken) ? releaseId : null;
        }
    }
}
