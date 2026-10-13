using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>The bytes of a cover, with what the API needs to answer: type, tag and who may cache it.</summary>
public sealed record CoverFile(Stream Content, string ContentType, string ETag, bool IsPrivate);

/// <summary>
/// Use case: open a cover image. The asset id is resolved through the database, whose asset filter hides a library-private
/// asset from everyone outside that library, so another library's asset is a plain 404. The file path comes from the
/// stored <c>blob_path</c> only, never from the request, and only when it passes <see cref="BlobPaths.IsSafe"/>.
/// </summary>
public static class GetCover
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    public sealed record Query(Guid AssetId);

    public sealed class Handler(IAppDbContext db, ICoverStore store)
    {
        public async Task<Result<CoverFile>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == query.AssetId, cancellationToken);
            if (asset is null || !ImageTypes.Contains(asset.ContentType) || !BlobPaths.IsSafe(asset.BlobPath))
            {
                return CatalogErrors.CoverNotFound;
            }

            var stream = await store.OpenReadAsync(asset.BlobPath, cancellationToken);
            return stream is null
                ? CatalogErrors.CoverNotFound
                : new CoverFile(stream, asset.ContentType.ToLowerInvariant(), $"\"{asset.Sha256}\"", asset.OwnerLibraryId is not null);
        }
    }
}
