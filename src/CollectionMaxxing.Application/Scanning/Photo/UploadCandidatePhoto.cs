using System.Security.Cryptography;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.Photo;

/// <summary>
/// Use case: "Use my photo". The phone sends a crop of one scanned item and it becomes that item's cover when the session is committed.
/// The caller's session and candidate are looked up first (a stranger learns nothing about what would have been accepted). The bytes must be a
/// sound JPEG, at most 4 MB and <see cref="JpegSanitizer.MaxDimension"/> pixels a side; they are stored rewritten without EXIF or any other
/// metadata, and the original bytes are not kept. The asset belongs to the session's library alone. A second upload for the same candidate
/// replaces the first. Nothing is stored for a refused file.
/// </summary>
public static class UploadCandidatePhoto
{
    public sealed record Command(Guid SessionId, Guid CandidateId, ReadOnlyMemory<byte> Content);

    public sealed class Handler(
        IAppDbContext db, ICurrentUser currentUser, IIdGenerator ids, IPhotoStore store, TimeProvider clock, ILogger<Handler> logger)
    {
        public async Task<Result<CandidateDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken) is not { } session)
            {
                return ScanningErrors.SessionNotFound;
            }

            var candidate = await db.ScanCandidates.FirstOrDefaultAsync(
                c => c.SessionId == session.Id && c.Id == command.CandidateId, cancellationToken);
            if (candidate is null)
            {
                return ScanningErrors.CandidateNotFound;
            }

            if (session.Status != ScanSessionStatus.Open)
            {
                return ScanningErrors.SessionClosed;
            }

            var cleaned = JpegSanitizer.Clean(command.Content.Span);
            if (!cleaned.IsSuccess)
            {
                return cleaned.Error!;
            }

            var jpeg = cleaned.Value;
            var now = clock.GetUtcNow();
            var assetId = ids.NewId();
            var blobPath = CandidatePhotos.BlobPathFor(assetId);

            // The snap the candidate points at now: if it already holds a photo it is ours and goes; otherwise it is the phone's own
            // record of the picture it analysed, whose facts the new snap copies and which the candidate stops pointing at.
            var previous = candidate.SnapId is { } previousId
                ? await db.ScanSnaps.FirstOrDefaultAsync(s => s.Id == previousId, cancellationToken)
                : null;
            PhotoFile? replacedPhoto = null;
            if (previous?.AssetId is { } replacedId)
            {
                var replaced = await db.Assets.FirstOrDefaultAsync(a => a.Id == replacedId, cancellationToken);
                if (replaced is not null)
                {
                    replacedPhoto = new PhotoFile(replaced.Id, replaced.BlobPath); // its row stays until the file is gone
                }

                // A kept row that cites the replaced snap stops citing it; the row itself stays (#345).
                await Provisional.ProvisionalLinks.ClearSnapLinksAsync(db, [previous.Id], cancellationToken);
                db.ScanSnaps.Remove(previous);
            }

            await store.SaveAsync(blobPath, jpeg.Bytes, cancellationToken);
            try
            {
                var snap = new ScanSnap
                {
                    Id = ids.NewId(),
                    LibraryId = session.LibraryId,
                    SessionId = session.Id,

                    // A crop the person chose to keep is a deliberate shutter press; the phone's own details of the picture, if it sent them, carry over.
                    Trigger = previous?.Trigger ?? SnapTrigger.Shutter,
                    Engine = previous?.Engine ?? ScanEngine.VisionPipeline,
                    FellBack = previous?.FellBack ?? false,
                    FallbackReason = previous?.FallbackReason,
                    ItemCount = 1,
                    AssetId = assetId,
                    CreatedAt = now,
                };
                db.Assets.Add(new Asset
                {
                    Id = assetId,
                    Kind = AssetKind.UserPhoto,
                    BlobPath = blobPath,
                    ContentType = "image/jpeg",
                    Width = jpeg.Width,
                    Height = jpeg.Height,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(jpeg.Bytes)),
                    OwnerLibraryId = session.LibraryId, // private: only this library's members can read it
                    CreatedAt = now,
                });
                db.ScanSnaps.Add(snap);
                candidate.SnapId = snap.Id;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                await store.TryDeleteAsync(blobPath, CancellationToken.None); // nothing points at the file; don't leave it behind
                throw;
            }

            if (replacedPhoto is { } old)
            {
                await CandidatePhotos.DeleteFilesAsync(db, store, logger, [old], cancellationToken);
            }

            return (await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken))[0];
        }
    }
}
