using CollectionMaxxing.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Locations;

/// <summary>Use case: list every location in a library. The tree is small, so there is only ever one page.</summary>
public static class ListLocations
{
    public sealed record Query(Guid LibraryId);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Page<LocationDto>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var items = await db.Locations
                .AsNoTracking()
                .Where(l => l.LibraryId == query.LibraryId)
                .OrderBy(l => l.SortOrder) // siblings keep this relative order; the client groups by parent
                .ThenBy(l => l.Name)
                .ThenBy(l => l.Id)
                .Select(l => new LocationDto(l.Id, l.ParentId, l.Name, l.SortOrder))
                .ToListAsync(cancellationToken);

            return new Page<LocationDto>(items);
        }
    }
}
