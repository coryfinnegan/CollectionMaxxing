using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Tags;

/// <summary>
/// Use case: list every tag in a library, sorted by name. The name column is citext, so the sort ignores case. A tag's item count is
/// the items it is on that are in the library (<see cref="ItemStatuses.InCollection"/>) and not in the trash: the same items the stats
/// and the item list, filtered by that tag, show.
/// </summary>
public static class ListTags
{
    public sealed record Query(Guid LibraryId);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Page<TagDto>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var items = await db.Tags
                .AsNoTracking()
                .Where(t => t.LibraryId == query.LibraryId)
                .OrderBy(t => t.Name)
                .ThenBy(t => t.Id)
                .Select(t => new TagDto(
                    t.Id,
                    t.Name,
                    db.ItemTags.Count(it => it.LibraryId == t.LibraryId && it.TagId == t.Id
                        && db.Items.Any(i => i.LibraryId == it.LibraryId && i.Id == it.ItemId && i.DeletedAt == null
                            && ItemStatuses.InCollection.Contains(i.Status)))))
                .ToListAsync(cancellationToken);

            return new Page<TagDto>(items);
        }
    }
}
