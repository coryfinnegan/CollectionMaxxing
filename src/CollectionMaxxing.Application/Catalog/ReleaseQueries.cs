using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>
/// Use case: one release by id. Another library's release looks like it doesn't exist (404, never 403). The barcodes listed are the
/// shared ones and the ones the caller's own libraries taught; without a known caller only the shared ones.
/// </summary>
public static class GetRelease
{
    public sealed record Query(Guid ReleaseId);

    public sealed class Handler(IAppDbContext db, ICurrentUser? currentUser = null)
    {
        public async Task<Result<ReleaseDto>> HandleAsync(Query query, CancellationToken cancellationToken) =>
            await ItemProjection.LoadReleaseAsync(db, currentUser?.LibraryIds ?? [], query.ReleaseId, cancellationToken) is { } loaded
                ? ItemProjection.ToReleaseDto(loaded)
                : CatalogErrors.ReleaseNotFound;
    }
}

/// <summary>Use case: the reference artwork of a release (its cover and its work's cover), when the caller may see them.</summary>
public static class ListReleaseArtwork
{
    public sealed record Query(Guid ReleaseId);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Result<Page<ArtworkDto>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            // No barcodes are shown here, so none of a library's own are needed.
            if (await ItemProjection.LoadReleaseAsync(db, [], query.ReleaseId, cancellationToken) is not { } loaded)
            {
                return CatalogErrors.ReleaseNotFound;
            }

            var ids = new[] { loaded.Release.CoverAssetId, loaded.Work.CoverAssetId }.OfType<Guid>().Distinct().ToList();

            // The asset filter drops a private asset the caller can't see, even if a visible release points at it.
            var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);
            var items = ids
                .Select(id => assets.FirstOrDefault(a => a.Id == id))
                .OfType<Domain.Catalog.Asset>()
                .Select(a => new ArtworkDto(a.Id, a.Kind, CatalogProjection.CoverUrl(a.Id), a.Width, a.Height))
                .ToList();
            return new Page<ArtworkDto>(items);
        }
    }
}

/// <summary>Use case: the games platforms (reference data).</summary>
public static class ListPlatforms
{
    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Page<PlatformDto>> HandleAsync(CancellationToken cancellationToken)
        {
            var items = await db.Platforms.AsNoTracking()
                .OrderBy(p => p.Name).ThenBy(p => p.Id)
                .Select(p => new PlatformDto(p.Id, p.Name, p.ShortName, p.Manufacturer))
                .ToListAsync(cancellationToken);
            return new Page<PlatformDto>(items);
        }
    }
}
