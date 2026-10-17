using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.Photo;

/// <summary>
/// How a candidate's "Use my photo" crop is kept. Each upload gets a snap of its own with <c>asset_id</c> set (a snap with an asset is always
/// that one candidate's), and the candidate points at it. So "has a photo" is "its snap has an asset", and the asset is private to the library.
/// </summary>
internal static class CandidatePhotos
{
    /// <summary>The blob path of a photo asset. The name is ours (the asset id), never anything the client sent.</summary>
    public static string BlobPathFor(Guid assetId)
    {
        var name = assetId.ToString("N");
        return $"photos/{name[..2]}/{name}.jpg";
    }

    /// <summary>The photo asset of each of the candidates that has one, by candidate id.</summary>
    public static async Task<Dictionary<Guid, Guid>> AssetIdsAsync(
        IAppDbContext db, IReadOnlyCollection<ScanCandidate> candidates, CancellationToken cancellationToken)
    {
        var snapIds = candidates.Select(c => c.SnapId).OfType<Guid>().Distinct().ToList();
        if (snapIds.Count == 0)
        {
            return [];
        }

        var snaps = await db.ScanSnaps.AsNoTracking()
            .Where(s => snapIds.Contains(s.Id) && s.AssetId != null)
            .Select(s => new { s.Id, AssetId = s.AssetId!.Value })
            .ToDictionaryAsync(s => s.Id, s => s.AssetId, cancellationToken);
        return candidates
            .Where(c => c.SnapId is { } snapId && snaps.ContainsKey(snapId))
            .ToDictionary(c => c.Id, c => snaps[c.SnapId!.Value]);
    }

    /// <summary>
    /// Takes the photos off these candidates: their snaps are removed from the change set and the candidates stop pointing at the snaps.
    /// The asset rows stay for now, as the record of the files. Returns the photos to delete once the change is saved
    /// (<see cref="DeleteFilesAsync"/>), which removes each asset row only when its file is gone. A file that is stuck keeps its row, and the
    /// daily clean-up (<see cref="SweepOrphansAsync"/>) tries again; a crash in between leaves the same record.
    /// </summary>
    public static async Task<List<PhotoFile>> DiscardAsync(
        IAppDbContext db, IReadOnlyCollection<ScanCandidate> candidates, CancellationToken cancellationToken)
    {
        var snapIds = candidates.Select(c => c.SnapId).OfType<Guid>().Distinct().ToList();
        if (snapIds.Count == 0)
        {
            return [];
        }

        var snaps = await db.ScanSnaps.Where(s => snapIds.Contains(s.Id) && s.AssetId != null).ToListAsync(cancellationToken);
        if (snaps.Count == 0)
        {
            return [];
        }

        var assetIds = snaps.Select(s => s.AssetId!.Value).ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).ToListAsync(cancellationToken);
        var photoSnapIds = snaps.Select(s => s.Id).ToHashSet();
        foreach (var candidate in candidates.Where(c => c.SnapId is { } snapId && photoSnapIds.Contains(snapId)))
        {
            candidate.SnapId = null;
        }

        db.ScanSnaps.RemoveRange(snaps);
        return assets.Select(a => new PhotoFile(a.Id, a.BlobPath)).ToList();
    }

    /// <summary>
    /// Deletes the files of photos whose snaps are already gone, then their asset rows. A file that is stuck is logged by path and its row is
    /// kept, so <see cref="SweepOrphansAsync"/> finds it again.
    /// </summary>
    public static async Task DeleteFilesAsync(
        IAppDbContext db, IPhotoStore store, ILogger logger, IReadOnlyCollection<PhotoFile> photos, CancellationToken cancellationToken)
    {
        var gone = new List<Guid>();
        foreach (var photo in photos)
        {
            if (await store.TryDeleteAsync(photo.BlobPath, cancellationToken))
            {
                gone.Add(photo.AssetId);
            }
            else
            {
                logger.LogWarning("Could not delete the photo file {BlobPath}; it is tried again by the daily clean-up.", photo.BlobPath);
            }
        }

        if (gone.Count > 0)
        {
            await db.Assets.IgnoreQueryFilters().Where(a => gone.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Retries the files of photos nothing uses any more (no snap, no item cover): deletes each file, then its row. A photo that is still
    /// stuck stays for tomorrow.
    /// </summary>
    public static async Task SweepOrphansAsync(IAppDbContext db, IPhotoStore store, ILogger logger, CancellationToken cancellationToken)
    {
        var orphans = await db.Assets.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Kind == AssetKind.UserPhoto
                && !db.ScanSnaps.IgnoreQueryFilters().Any(s => s.AssetId == a.Id)
                && !db.Items.IgnoreQueryFilters().Any(i => i.CustomCoverAssetId == a.Id)
                && !db.Works.IgnoreQueryFilters().Any(w => w.CoverAssetId == a.Id)
                && !db.Releases.IgnoreQueryFilters().Any(r => r.CoverAssetId == a.Id))
            .Select(a => new PhotoFile(a.Id, a.BlobPath))
            .ToListAsync(cancellationToken);
        await DeleteFilesAsync(db, store, logger, orphans, cancellationToken);
    }
}

/// <summary>A stored photo: its asset row and the file it names.</summary>
internal readonly record struct PhotoFile(Guid AssetId, string BlobPath);
