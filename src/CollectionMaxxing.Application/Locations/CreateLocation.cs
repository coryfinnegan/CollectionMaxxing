using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Locations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Locations;

/// <summary>Use case: add a location, at the top level or inside another one.</summary>
public static class CreateLocation
{
    public sealed record Command(Guid LibraryId, Guid? ParentId, string? Name, int? SortOrder);

    public sealed class Handler(IAppDbContext db, IIdGenerator ids)
    {
        public async Task<Result<LocationDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            // The API already checked membership. This is the second lock: the library filter makes a library the
            // caller can't see look like it doesn't exist.
            if (!await db.Libraries.AnyAsync(l => l.Id == command.LibraryId, cancellationToken))
            {
                return Errors.LibraryNotFound;
            }

            var existing = await db.Locations.Where(l => l.LibraryId == command.LibraryId).ToListAsync(cancellationToken);
            var added = new LocationTree(command.LibraryId, existing)
                .Add(ids.NewId(), command.ParentId, command.Name, command.SortOrder);
            if (added.Error is { } error)
            {
                return error;
            }

            db.Locations.Add(added.Value);
            await db.SaveChangesAsync(cancellationToken);
            return LocationDto.From(added.Value);
        }
    }
}
