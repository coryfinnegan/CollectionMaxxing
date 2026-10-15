using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>Use case: soft-delete an item. It stays in "Recently deleted" for 30 days, then is purged.</summary>
public static class DeleteItem
{
    public sealed record Command(Guid LibraryId, Guid Id, string? IfMatch);

    public sealed class Handler(IAppDbContext db, TimeProvider clock, IAuditLog audit)
    {
        public async Task<Result> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var item = await db.Items.FirstOrDefaultAsync(
                i => i.LibraryId == command.LibraryId && i.Id == command.Id && i.DeletedAt == null, cancellationToken);
            if (item is null)
            {
                return Result.Failure(ItemErrors.NotFound);
            }

            if (!ItemVersion.Matches(command.IfMatch, item.Version))
            {
                return Result.Failure(ItemErrors.PreconditionFailed);
            }

            var now = clock.GetUtcNow();
            item.DeletedAt = now;
            item.UpdatedAt = now;
            audit.Record(AuditActions.ItemDeleted, "item", item.Id, item.LibraryId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure(ItemErrors.PreconditionFailed);
            }

            return Result.Success();
        }
    }
}
