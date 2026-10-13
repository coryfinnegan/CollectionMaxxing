using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>Use case: edit an item (a partial update). The caller must hold the current version (<c>If-Match</c>).</summary>
public static class UpdateItem
{
    public sealed record Command(Guid LibraryId, Guid Id, string? IfMatch, ItemPatch Patch);

    public sealed class Handler(IAppDbContext db, TimeProvider clock)
    {
        public async Task<Result<Versioned<ItemDto>>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var libraryId = command.LibraryId;
            var item = await db.Items.FirstOrDefaultAsync(
                i => i.LibraryId == libraryId && i.Id == command.Id && i.DeletedAt == null, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            if (!ItemVersion.Matches(command.IfMatch, item.Version))
            {
                return ItemErrors.PreconditionFailed;
            }

            var patch = command.Patch;
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

            if (patch.Notes.IsSet && patch.Notes.Value is { Length: > Item.MaxNotesLength })
            {
                return ItemErrors.Invalid("item.notes_too_long", $"Notes can be at most {Item.MaxNotesLength} characters.", "notes");
            }

            if (patch.CustomTitle.IsSet && patch.CustomTitle.Value is { Length: > CreateItem.MaxTitleLength })
            {
                return ItemErrors.Invalid("item.title_too_long", $"A title can be at most {CreateItem.MaxTitleLength} characters.", "customTitle");
            }

            if (patch.LentTo.IsSet && patch.LentTo.Value is { Length: > 200 })
            {
                return ItemErrors.Invalid("item.lent_to_too_long", "Who it is lent to can be at most 200 characters.", "lentTo");
            }

            if (patch.LocationId is { IsSet: true, Value: { } locationId }
                && !await ItemProjection.LocationInLibraryAsync(db, libraryId, locationId, cancellationToken))
            {
                return ItemErrors.LocationNotFound;
            }

            List<Guid>? tagIds = null;
            if (patch.TagIds is not null)
            {
                tagIds = patch.TagIds.Distinct().ToList();
                if (!await ItemProjection.TagsInLibraryAsync(db, libraryId, tagIds, cancellationToken))
                {
                    return ItemErrors.TagNotFound;
                }
            }

            var status = patch.Status ?? item.Status;
            if (patch.LentTo.IsSet && !string.IsNullOrWhiteSpace(patch.LentTo.Value))
            {
                // Lending something sets it to Lent, unless it is already gone.
                if (status == ItemStatus.Sold)
                {
                    return ItemErrors.Invalid("item.sold_cannot_be_lent", "A sold item can't be lent out.", "lentTo");
                }

                if (patch.Status is null)
                {
                    status = ItemStatus.Lent;
                }

                item.LentOn = patch.LentOn.IsSet ? patch.LentOn.Value : item.LentOn ?? today;
                item.LentTo = patch.LentTo.Value!.Trim();
            }
            else
            {
                if (patch.LentTo.IsSet)
                {
                    item.LentTo = null;
                }

                if (patch.LentOn.IsSet)
                {
                    item.LentOn = patch.LentOn.Value;
                }
            }

            item.Status = status;
            if (patch.Condition.IsSet)
            {
                item.Condition = patch.Condition.Value;
            }

            if (patch.Completeness.IsSet)
            {
                item.Completeness = patch.Completeness.Value;
            }

            if (patch.LocationId.IsSet)
            {
                item.LocationId = patch.LocationId.Value;
            }

            if (patch.Notes.IsSet)
            {
                item.Notes = patch.Notes.Value;
            }

            if (patch.AcquiredOn.IsSet)
            {
                item.AcquiredOn = patch.AcquiredOn.Value;
            }

            if (patch.CustomTitle.IsSet)
            {
                item.CustomTitle = string.IsNullOrWhiteSpace(patch.CustomTitle.Value) ? null : patch.CustomTitle.Value.Trim();
            }

            if (patch.CoverChoice is { } coverChoice)
            {
                item.CoverChoice = coverChoice;
            }

            if (tagIds is not null)
            {
                var existing = await db.ItemTags.Where(t => t.LibraryId == libraryId && t.ItemId == item.Id).ToListAsync(cancellationToken);
                db.ItemTags.RemoveRange(existing.Where(t => !tagIds.Contains(t.TagId)));
                foreach (var tagId in tagIds.Where(id => existing.All(t => t.TagId != id)))
                {
                    db.ItemTags.Add(new ItemTag { LibraryId = libraryId, ItemId = item.Id, TagId = tagId });
                }
            }

            item.UpdatedAt = clock.GetUtcNow();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ItemErrors.PreconditionFailed; // someone saved between our read and our write
            }

            var currentTags = await ItemProjection.TagIdsAsync(db, libraryId, item.Id, cancellationToken);
            return new Versioned<ItemDto>(ItemProjection.ToDto(item, currentTags), item.Version);
        }
    }
}
