using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>Use case: one item with its work and release, and the other copies of that work. Deleted items are not found.</summary>
public static class GetItem
{
    public sealed record Query(Guid LibraryId, Guid Id);

    public sealed class Handler(IAppDbContext db, IUserDirectory? users = null)
    {
        public async Task<Result<Versioned<ItemDetailDto>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var item = await db.Items.AsNoTracking()
                .FirstOrDefaultAsync(i => i.LibraryId == query.LibraryId && i.Id == query.Id && i.DeletedAt == null, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            var loaded = await ItemProjection.LoadReleaseAsync(db, item.ReleaseId, cancellationToken);
            if (loaded is null)
            {
                return ItemErrors.NotFound;
            }

            var tagIds = await ItemProjection.TagIdsAsync(db, query.LibraryId, item.Id, cancellationToken);

            var otherCopies = await (
                from other in db.Items.AsNoTracking()
                join release in db.Releases on other.ReleaseId equals release.Id
                join platform in db.Platforms on release.PlatformId equals platform.Id into platforms
                from platform in platforms.DefaultIfEmpty()
                where other.LibraryId == query.LibraryId && other.DeletedAt == null && other.Id != item.Id && release.WorkId == loaded.Work.Id
                orderby other.CreatedAt, other.Id
                select new OtherCopyDto(other.Id, release.Id, release.Format, platform == null ? null : platform.Name, other.Status, other.LocationId))
                .ToListAsync(cancellationToken);

            var detail = new ItemDetailDto(
                await ItemProjection.ToDtoAsync(item, tagIds, users, cancellationToken),
                ItemProjection.ToWorkDto(loaded.Work),
                ItemProjection.ToReleaseDto(loaded),
                otherCopies);
            return new Versioned<ItemDetailDto>(detail, item.Version);
        }
    }
}
