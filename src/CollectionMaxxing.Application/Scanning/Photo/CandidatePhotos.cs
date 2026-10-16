using CollectionMaxxing.Application.Common;
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
    /// Takes the photos off these candidates: their snaps and assets are removed from the change set and the candidates stop pointing at the
    /// snaps. Returns the files to delete once the change is saved (files go after the rows, so a failed save loses nothing).
    /// </summary>
    public static async Task<List<string>> DiscardAsync(
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
        var assets = await db.Assets.Where(a => assetIds.Contains(a.Id)).ToListAsync(cancellationToken);
        var photoSnapIds = snaps.Select(s => s.Id).ToHashSet();
        foreach (var candidate in candidates.Where(c => c.SnapId is { } snapId && photoSnapIds.Contains(snapId)))
        {
            candidate.SnapId = null;
        }

        db.ScanSnaps.RemoveRange(snaps);
        db.Assets.RemoveRange(assets);
        return assets.Select(a => a.BlobPath).ToList();
    }

    /// <summary>Deletes the files of photos whose rows are already gone. A file that is stuck is logged by path; nothing points at it any more.</summary>
    public static async Task DeleteFilesAsync(IPhotoStore store, ILogger logger, IEnumerable<string> blobPaths, CancellationToken cancellationToken)
    {
        foreach (var path in blobPaths)
        {
            if (!await store.TryDeleteAsync(path, cancellationToken))
            {
                logger.LogWarning("Could not delete the photo file {BlobPath}; it is left on disk with no record pointing at it.", path);
            }
        }
    }
}
