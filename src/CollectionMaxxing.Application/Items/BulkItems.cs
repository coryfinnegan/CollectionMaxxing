using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

public enum BulkOperation
{
    Patch,
    Delete,
}

/// <summary>The change a bulk patch applies to every listed item.</summary>
public sealed record BulkChange(Guid? LocationId, ItemStatus? Status, IReadOnlyList<Guid>? AddTagIds, IReadOnlyList<Guid>? RemoveTagIds);

/// <summary>Use case: patch or delete many items at once. All or nothing: one id that isn't in this library fails the whole call.</summary>
public static class BulkItems
{
    public const int MaxIds = 500;

    public sealed record Command(Guid LibraryId, IReadOnlyList<Guid>? Ids, BulkOperation? Operation, BulkChange? Patch);

    public sealed class Handler(IAppDbContext db, TimeProvider clock, IAuditLog audit)
    {
        public async Task<Result<BulkItemsResultDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var libraryId = command.LibraryId;
            if (command.Ids is not { Count: > 0 })
            {
                return ItemErrors.Invalid("bulk.ids_required", "List the items to change.", "ids");
            }

            if (command.Ids.Count > MaxIds)
            {
                return ItemErrors.Invalid("bulk.too_many_ids", $"A bulk change can include at most {MaxIds} items.", "ids");
            }

            if (command.Operation is not { } operation)
            {
                return ItemErrors.Invalid("bulk.action_required", "Say whether to patch or delete.", "action");
            }

            var patch = command.Patch;
            if (operation == BulkOperation.Patch
                && (patch is null || (patch.LocationId is null && patch.Status is null
                    && patch.AddTagIds is not { Count: > 0 } && patch.RemoveTagIds is not { Count: > 0 })))
            {
                return ItemErrors.Invalid("bulk.patch_required", "Say what to change.", "patch");
            }

            var ids = command.Ids.Distinct().ToList();
            var items = await db.Items
                .Where(i => i.LibraryId == libraryId && i.DeletedAt == null && ids.Contains(i.Id))
                .ToListAsync(cancellationToken);
            if (items.Count != ids.Count)
            {
                return ItemErrors.NotFound; // not one of this library's items: nothing changes
            }

            var now = clock.GetUtcNow();
            if (operation == BulkOperation.Delete)
            {
                foreach (var item in items)
                {
                    item.DeletedAt = now;
                    item.UpdatedAt = now;
                }

                audit.Record(
                    AuditActions.ItemsBulkDeleted, "item", null, libraryId, new Dictionary<string, object> { ["count"] = items.Count });
            }
            else
            {
                var failure = await ApplyPatchAsync(items, patch!, libraryId, now, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }
            }

            await db.SaveChangesAsync(cancellationToken); // one transaction
            return new BulkItemsResultDto(items.Count);
        }

        private async Task<DomainError?> ApplyPatchAsync(
            List<Item> items, BulkChange patch, Guid libraryId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var add = patch.AddTagIds?.Distinct().ToList() ?? [];
            var remove = patch.RemoveTagIds?.Distinct().ToList() ?? [];

            if (patch.LocationId is { } locationId
                && !await ItemProjection.LocationInLibraryAsync(db, libraryId, locationId, cancellationToken))
            {
                return ItemErrors.LocationNotFound;
            }

            if (!await ItemProjection.TagsInLibraryAsync(db, libraryId, add.Union(remove).ToList(), cancellationToken))
            {
                return ItemErrors.TagNotFound;
            }

            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var itemIds = items.Select(i => i.Id).ToList();
            var existing = add.Count + remove.Count == 0
                ? []
                : await db.ItemTags.Where(t => t.LibraryId == libraryId && itemIds.Contains(t.ItemId)).ToListAsync(cancellationToken);

            foreach (var item in items)
            {
                if (patch.LocationId is { } newLocation)
                {
                    item.LocationId = newLocation;
                }

                if (patch.Status is { } status)
                {
                    item.Status = status;
                    if (status == ItemStatus.Lent)
                    {
                        // Lending starts today, with no borrower named (the edit form can add one); an existing lend keeps its own.
                        item.LentOn ??= today;
                    }
                    else
                    {
                        // An item that isn't lent out has no borrower, so a later lend never shows the old one.
                        item.LentTo = null;
                        item.LentOn = null;
                    }
                }

                item.UpdatedAt = now;
                foreach (var tagId in add.Where(t => !existing.Any(e => e.ItemId == item.Id && e.TagId == t)))
                {
                    db.ItemTags.Add(new ItemTag { LibraryId = libraryId, ItemId = item.Id, TagId = tagId });
                }
            }

            db.ItemTags.RemoveRange(existing.Where(e => remove.Contains(e.TagId) && !add.Contains(e.TagId)));
            return null;
        }
    }
}
