using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.Learning;

/// <summary>
/// Recurring job (daily): removes scan sessions nobody committed once they are 7 days old, with their candidates, snaps and the photos
/// uploaded for them ("Use my photo": the asset rows and the files). A committed session stays: its candidates say where the items came
/// from, and its photos are the items' covers. A session whose photo file is stuck (a virus scanner has it open, say) stays too, and
/// the next run tries again, so a file is never left on disk with nothing that remembers it.
/// </summary>
public sealed class CleanUpScanSessions(IAppDbContext db, TimeProvider clock, IPhotoStore photoStore, ILogger<CleanUpScanSessions> logger)
    : IJobHandler<CleanUpScanSessions.Job>
{
    public const string JobType = "scan.clean_up_sessions";

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Recurring jobs carry no data.</summary>
    public sealed record Job;

    public async Task HandleAsync(Job payload, CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - MaxAge;

        // A background job has no signed-in caller, so the library filter is off on purpose.
        var expired = await db.ScanSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Status != ScanSessionStatus.Committed && s.CreatedAt < cutoff)
            .Where(s => !db.ScanCandidates.IgnoreQueryFilters().Any(c => c.SessionId == s.Id && c.CreatedItemId != null)) // it made items: it was committed, whatever its status says
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            await CandidatePhotos.SweepOrphansAsync(db, photoStore, logger, cancellationToken);
            return;
        }

        // Photos first: a file goes before the row that names it, so a crash leaves a row without a file (found again tomorrow), never
        // a file nothing points to. An asset some item already uses as its cover is not ours to delete.
        var photos = await (
                from snap in db.ScanSnaps.IgnoreQueryFilters().AsNoTracking()
                where expired.Contains(snap.SessionId) && snap.AssetId != null
                join asset in db.Assets.IgnoreQueryFilters().AsNoTracking() on snap.AssetId equals asset.Id
                where !db.Items.IgnoreQueryFilters().Any(i => i.CustomCoverAssetId == asset.Id)
                select new { snap.SessionId, AssetId = asset.Id, asset.BlobPath })
            .ToListAsync(cancellationToken);
        var stuckSessions = new HashSet<Guid>();
        var gone = new List<Guid>();
        foreach (var photo in photos)
        {
            if (await photoStore.TryDeleteAsync(photo.BlobPath, cancellationToken))
            {
                gone.Add(photo.AssetId);
            }
            else
            {
                stuckSessions.Add(photo.SessionId);
                logger.LogWarning("Could not delete the photo file {BlobPath} of an old scan session; it is tried again tomorrow.", photo.BlobPath);
            }
        }

        // A session with a stuck file keeps its snaps, and so the rows of its other photos (their files are already gone: that is fine).
        var removable = expired.Where(id => !stuckSessions.Contains(id)).ToList();
        var removableAssets = photos.Where(p => removable.Contains(p.SessionId) && gone.Contains(p.AssetId)).Select(p => p.AssetId).ToList();

        // Candidates cite snaps (restrict), so they go first; sessions then take what is left with them.
        await db.ScanCandidates.IgnoreQueryFilters().Where(c => removable.Contains(c.SessionId)).ExecuteDeleteAsync(cancellationToken);
        await db.ScanSnaps.IgnoreQueryFilters().Where(s => removable.Contains(s.SessionId)).ExecuteDeleteAsync(cancellationToken);
        await db.ScanSessions.IgnoreQueryFilters().Where(s => removable.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken);
        await db.Assets.IgnoreQueryFilters().Where(a => removableAssets.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);

        await CandidatePhotos.SweepOrphansAsync(db, photoStore, logger, cancellationToken);
    }
}
