using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Locations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Locations;

/// <summary>Use case: rename, move or reorder a location (a partial update).</summary>
public static class UpdateLocation
{
    public sealed record Command(Guid LibraryId, Guid Id, LocationPatch Patch);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Result<LocationDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var existing = await db.Locations.Where(l => l.LibraryId == command.LibraryId).ToListAsync(cancellationToken);
            var updated = new LocationTree(command.LibraryId, existing).Update(command.Id, command.Patch);
            if (updated.Error is { } error)
            {
                return error;
            }

            await db.SaveChangesAsync(cancellationToken); // EF notices what the tree changed
            return LocationDto.From(updated.Value);
        }
    }
}
