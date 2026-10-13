using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>Use case: swap an item's release and keep everything else (condition, location, notes, tags).</summary>
public static class RematchItem
{
    public sealed record Command(Guid LibraryId, Guid Id, Guid? ReleaseId);

    public sealed class Handler(IAppDbContext db, TimeProvider clock)
    {
        public async Task<Result<Versioned<ItemDto>>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (command.ReleaseId is not { } releaseId)
            {
                return ItemErrors.Invalid("item.release_required", "Say which release to switch to.", "releaseId");
            }

            var item = await db.Items.FirstOrDefaultAsync(
                i => i.LibraryId == command.LibraryId && i.Id == command.Id && i.DeletedAt == null, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            // A private release of another library looks exactly like one that doesn't exist.
            if (!await ItemProjection.CanUseReleaseAsync(db, command.LibraryId, releaseId, cancellationToken))
            {
                return ItemErrors.ReleaseNotFound;
            }

            item.ReleaseId = releaseId;
            item.UpdatedAt = clock.GetUtcNow();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ItemErrors.PreconditionFailed;
            }

            var tagIds = await ItemProjection.TagIdsAsync(db, command.LibraryId, item.Id, cancellationToken);
            return new Versioned<ItemDto>(ItemProjection.ToDto(item, tagIds), item.Version);
        }
    }
}
