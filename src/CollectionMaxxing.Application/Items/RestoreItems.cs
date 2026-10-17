using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>
/// Use case: undo the delete of many items at once (Review's Undo after a commit). All or nothing, in one transaction: an id
/// that isn't one of this library's items, or that was deleted more than 30 days ago, fails the whole call. An id that is
/// already live is left alone, so repeating the call is harmless.
/// </summary>
public static class RestoreItems
{
    public sealed record Command(Guid LibraryId, IReadOnlyList<Guid>? Ids);

    public sealed class Handler(IAppDbContext db, TimeProvider clock, IAuditLog audit)
    {
        public async Task<Result<BulkItemsResultDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (command.Ids is not { Count: > 0 })
            {
                return ItemErrors.Invalid("bulk.ids_required", "List the items to restore.", "ids");
            }

            if (command.Ids.Count > BulkItems.MaxIds)
            {
                return ItemErrors.Invalid("bulk.too_many_ids", $"A bulk change can include at most {BulkItems.MaxIds} items.", "ids");
            }

            var libraryId = command.LibraryId;
            var ids = command.Ids.Distinct().ToList();
            var items = await db.Items.Where(i => i.LibraryId == libraryId && ids.Contains(i.Id)).ToListAsync(cancellationToken);
            if (items.Count != ids.Count)
            {
                return ItemErrors.NotFound; // not one of this library's items: nothing changes
            }

            var now = clock.GetUtcNow();
            var deleted = items.Where(i => i.DeletedAt is not null).ToList();
            if (deleted.Any(i => now - i.DeletedAt!.Value > RestoreItem.Window))
            {
                return ItemErrors.RestoreWindowClosed;
            }

            if (deleted.Count > 0)
            {
                foreach (var item in deleted)
                {
                    item.DeletedAt = null;
                    item.UpdatedAt = now;
                }

                audit.Record(
                    AuditActions.ItemsBulkRestored, "item", null, libraryId, new Dictionary<string, object> { ["count"] = deleted.Count });
                await db.SaveChangesAsync(cancellationToken); // one transaction
            }

            return new BulkItemsResultDto(items.Count);
        }
    }
}
