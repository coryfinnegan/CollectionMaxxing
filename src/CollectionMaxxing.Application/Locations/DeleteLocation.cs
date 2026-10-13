using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Locations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Locations;

/// <summary>Use case: delete a location that has nothing inside it.</summary>
public static class DeleteLocation
{
    public sealed record Command(Guid LibraryId, Guid Id);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Result> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var existing = await db.Locations.Where(l => l.LibraryId == command.LibraryId).ToListAsync(cancellationToken);
            var removed = new LocationTree(command.LibraryId, existing).Remove(command.Id);
            if (!removed.IsSuccess)
            {
                return removed;
            }

            db.Locations.Remove(existing.Single(l => l.Id == command.Id));
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }
}
