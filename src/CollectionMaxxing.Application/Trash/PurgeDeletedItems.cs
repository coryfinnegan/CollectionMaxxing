using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Trash;

public sealed class PurgeDeletedItemsPayload;

/// <summary>
/// Recurring job: items deleted more than 30 days ago are removed for good, with their dependent rows (tags and feature prints
/// cascade in the database; a scan candidate that created the item just forgets it). Nothing newer is touched.
/// Safe to run twice: a second run finds nothing.
/// </summary>
public static class PurgeDeletedItems
{
    public const string JobType = "trash.purge";
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);
    private const int BatchSize = 500;

    public sealed class Handler(IAppDbContext db, TimeProvider clock, IAuditLog audit) : IJobHandler<PurgeDeletedItemsPayload>
    {
        public async Task HandleAsync(PurgeDeletedItemsPayload payload, CancellationToken cancellationToken)
        {
            // Same rule as RestoreItem: restorable while now - deletedAt <= Window, purgeable once it is over.
            var cutoff = clock.GetUtcNow() - RestoreItem.Window;
            while (true)
            {
                // A background job has no signed-in caller, so the library filter is off on purpose.
                var batch = await db.Items.IgnoreQueryFilters()
                    .Where(i => i.DeletedAt != null && i.DeletedAt < cutoff)
                    .OrderBy(i => i.DeletedAt).ThenBy(i => i.Id)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);
                if (batch.Count == 0)
                {
                    // The same 30 days for the records of items removed for good: a phone offline longer than that does a full sync anyway.
                    await db.ItemTombstones.IgnoreQueryFilters().Where(t => t.DeletedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
                    return;
                }

                var ids = batch.Select(i => i.Id).ToList();
                await db.ScanCandidates.IgnoreQueryFilters()
                    .Where(c => c.CreatedItemId != null && ids.Contains(c.CreatedItemId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedItemId, (Guid?)null), cancellationToken);

                foreach (var item in batch)
                {
                    db.Items.Remove(item);
                    audit.Record(AuditActions.ItemPurged, "item", item.Id, item.LibraryId);
                }

                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
