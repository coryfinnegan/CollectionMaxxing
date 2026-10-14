using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>Use case: undo a delete, within 30 days. Restoring an item that isn't deleted just returns it.</summary>
public static class RestoreItem
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public sealed record Command(Guid LibraryId, Guid Id);

    public sealed class Handler(IAppDbContext db, TimeProvider clock, IAuditLog audit, IUserDirectory? users = null)
    {
        public async Task<Result<Versioned<ItemDto>>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var item = await db.Items.FirstOrDefaultAsync(i => i.LibraryId == command.LibraryId && i.Id == command.Id, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            var now = clock.GetUtcNow();
            if (item.DeletedAt is { } deletedAt)
            {
                if (now - deletedAt > Window)
                {
                    return ItemErrors.RestoreWindowClosed;
                }

                item.DeletedAt = null;
                item.UpdatedAt = now;
                audit.Record(AuditActions.ItemRestored, "item", item.Id, item.LibraryId);
                await db.SaveChangesAsync(cancellationToken);
            }

            var tagIds = await ItemProjection.TagIdsAsync(db, command.LibraryId, item.Id, cancellationToken);
            return new Versioned<ItemDto>(await ItemProjection.ToDtoAsync(item, tagIds, users, cancellationToken), item.Version);
        }
    }
}
