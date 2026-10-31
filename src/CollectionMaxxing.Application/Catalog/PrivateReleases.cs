using CollectionMaxxing.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>
/// Cleans up what a library made for itself in the catalogue (a hand-typed item's release and work) once nothing of the library
/// points at it any more. A release or work the shared catalogue uses is never touched. Callers run without a signed-in caller
/// (background jobs), so the library filter is off on purpose.
/// </summary>
internal static class PrivateReleases
{
    /// <summary>
    /// Removes the library-made releases among <paramref name="releaseIds"/> that no item points at (a deleted item still does until it
    /// is purged) and no feature print was matched against, then the works that leaves without a release. A scan candidate that matched
    /// a removed release forgets it; the release's barcodes go with it.
    /// </summary>
    public static async Task RemoveUnusedAsync(IAppDbContext db, List<Guid> releaseIds, CancellationToken cancellationToken)
    {
        var unused = await db.Releases.IgnoreQueryFilters().AsNoTracking()
            .Where(r => releaseIds.Contains(r.Id)
                && r.OwnerLibraryId != null
                && !db.Items.IgnoreQueryFilters().Any(i => i.ReleaseId == r.Id)
                && !db.ItemFeaturePrints.IgnoreQueryFilters().Any(p => p.ReleaseId == r.Id))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        if (unused.Count == 0)
        {
            return;
        }

        await db.ScanCandidates.IgnoreQueryFilters()
            .Where(c => c.MatchedReleaseId != null && unused.Contains(c.MatchedReleaseId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.MatchedReleaseId, (Guid?)null), cancellationToken);
        await db.Releases.IgnoreQueryFilters().Where(r => unused.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
        await DeleteWorksWithoutReleasesAsync(db, cancellationToken);
    }

    /// <summary>
    /// Deletes every work that no release points at, whoever owns the releases it had. Finding them by what is missing, not by a list taken
    /// before the releases went, means a run that stopped between the two steps is finished by the next one.
    /// </summary>
    public static Task DeleteWorksWithoutReleasesAsync(IAppDbContext db, CancellationToken cancellationToken) =>
        db.Works
            .Where(w => !db.Releases.IgnoreQueryFilters().Any(r => r.WorkId == w.Id))
            .ExecuteDeleteAsync(cancellationToken);
}
