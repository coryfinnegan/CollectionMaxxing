using System.Security.Cryptography;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Provisional;

/// <summary>Where a segment crop is kept: its own folder, never anything the client sent in the name.</summary>
internal static class CropPhotos
{
    public static string BlobPathFor(Guid assetId)
    {
        var name = assetId.ToString("N");
        return $"crops/{name[..2]}/{name}.jpg";
    }
}

/// <summary>
/// Use case: upload one row's segment crop (ADR-030). Only for rows the person keeps: an accepted or already-added
/// candidate (a success) is refused, so successes never upload bytes. The crop is stored sanitized as a library-private
/// <c>AssetKind.ScanPhoto</c> and claimed by keeping the row; an upload nobody claims is swept as an orphan.
/// </summary>
public static class UploadCandidateCrop
{
    public sealed record Command(Guid SessionId, Guid CandidateId, ReadOnlyMemory<byte> Content);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, IIdGenerator ids, IPhotoStore store, TimeProvider clock)
    {
        public async Task<Result<CropUploadDto>> HandleAsync(Command command, CancellationToken cancellationToken)
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

            if (candidate.Decision == CandidateDecision.Accepted || candidate.CreatedItemId is not null)
            {
                return new DomainError(
                    ErrorKind.Conflict,
                    "scan.crop_success_refused",
                    "That item is already on its way into the library, so it needs no crop. Crops are only kept for rows that still need eyes.",
                    "candidateId");
            }

            var cleaned = JpegSanitizer.Clean(command.Content.Span);
            if (!cleaned.IsSuccess)
            {
                return cleaned.Error!;
            }

            var jpeg = cleaned.Value;
            var now = clock.GetUtcNow();
            var assetId = ids.NewId();
            var blobPath = CropPhotos.BlobPathFor(assetId);

            await store.SaveAsync(blobPath, jpeg.Bytes, cancellationToken);
            try
            {
                db.Assets.Add(new Asset
                {
                    Id = assetId,
                    Kind = AssetKind.ScanPhoto,
                    BlobPath = blobPath,
                    ContentType = "image/jpeg",
                    Width = jpeg.Width,
                    Height = jpeg.Height,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(jpeg.Bytes)),
                    OwnerLibraryId = session.LibraryId, // private: only this library's members can read it
                    CreatedAt = now,
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                await store.TryDeleteAsync(blobPath, CancellationToken.None); // nothing points at the file; don't leave it behind
                throw;
            }

            return new CropUploadDto(assetId, $"/media/covers/{assetId}");
        }
    }
}
