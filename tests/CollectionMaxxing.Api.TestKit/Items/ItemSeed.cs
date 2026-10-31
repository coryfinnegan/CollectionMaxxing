using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Items;

/// <summary>Puts known catalogue rows, items and tags in the database for the items and libraries tests.</summary>
public static class ItemSeed
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public static async Task<Guid> AddPlatformAsync(PostgresFixture db, string name, string shortName)
    {
        var platform = new Platform { Id = Guid.CreateVersion7(), Name = name, ShortName = shortName };
        await using var context = db.CreateContext();
        context.Platforms.Add(platform);
        await context.SaveChangesAsync();
        return platform.Id;
    }

    public static async Task<Guid> AddTagAsync(PostgresFixture db, Guid libraryId, string name)
    {
        var tag = new Tag { Id = Guid.CreateVersion7(), LibraryId = libraryId, Name = name };
        await using var context = db.CreateContext();
        context.Tags.Add(tag);
        await context.SaveChangesAsync();
        return tag.Id;
    }

    public static async Task TagItemAsync(PostgresFixture db, Guid libraryId, Guid itemId, Guid tagId)
    {
        await using var context = db.CreateContext();
        context.ItemTags.Add(new ItemTag { LibraryId = libraryId, ItemId = itemId, TagId = tagId });
        await context.SaveChangesAsync();
    }

    /// <summary>A work with one release; a release owned by a library is private to it.</summary>
    public static async Task<Guid> AddReleaseAsync(
        PostgresFixture db,
        string title = "Jurassic Park",
        WorkKind kind = WorkKind.Film,
        ReleaseFormat format = ReleaseFormat.Dvd,
        int? year = 1993,
        Guid? platformId = null,
        Guid? ownerLibraryId = null,
        Guid? workId = null)
    {
        await using var context = db.CreateContext();
        if (workId is null)
        {
            var work = new Work
            {
                Id = Guid.CreateVersion7(),
                Kind = kind,
                Title = title,
                SortTitle = title.ToLowerInvariant(),
                NormalizedTitle = title.ToLowerInvariant(),
                OriginalYear = year,
                CreatedAt = Start,
            };
            context.Works.Add(work);
            workId = work.Id;
        }

        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = workId.Value,
            Format = format,
            PlatformId = platformId,
            Source = ReleaseSource.Provider,
            OwnerLibraryId = ownerLibraryId,
            CreatedAt = Start,
        };
        context.Releases.Add(release);
        await context.SaveChangesAsync();
        return release.Id;
    }

    public static async Task<Guid> WorkOfAsync(PostgresFixture db, Guid releaseId)
    {
        await using var context = db.CreateContext();
        return await context.Releases.IgnoreQueryFilters().Where(r => r.Id == releaseId).Select(r => r.WorkId).SingleAsync();
    }

    /// <summary>An item of a new release with the given traits. <paramref name="addedMinutes"/> orders items by "added at".</summary>
    public static async Task<Guid> AddItemAsync(
        PostgresFixture db,
        TestLibrary library,
        string title,
        WorkKind kind = WorkKind.Film,
        ReleaseFormat format = ReleaseFormat.Dvd,
        int? year = 1993,
        Guid? platformId = null,
        ItemStatus status = ItemStatus.Owned,
        Guid? locationId = null,
        int addedMinutes = 0)
    {
        var releaseId = await AddReleaseAsync(db, title, kind, format, year, platformId);
        var item = new Item
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ReleaseId = releaseId,
            Status = status,
            LocationId = locationId,
            CoverChoice = CoverChoice.Release,
            AddedVia = AddedVia.Manual,
            CreatedAt = Start.AddMinutes(addedMinutes),
            CreatedBy = library.OwnerId,
            UpdatedAt = Start.AddMinutes(addedMinutes),
        };
        await using var context = db.CreateContext();
        context.Items.Add(item);
        await context.SaveChangesAsync();
        return item.Id;
    }

    /// <summary>Names the borrower straight in the table (the item is then lent out, as the API keeps it).</summary>
    public static async Task SetLentToAsync(PostgresFixture db, Guid itemId, string borrower)
    {
        await using var context = db.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"update items set status = 'lent', lent_to = {borrower} where id = {itemId}");
    }

    /// <summary>Clears <c>edition_verified</c> straight in the table, to stand in for a T1 auto-add.</summary>
    public static async Task SetEditionUnverifiedAsync(PostgresFixture db, Guid itemId)
    {
        await using var context = db.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"update items set edition_verified = false where id = {itemId}");
    }

    /// <summary>Sets <c>deleted_at</c> straight in the table, to play out the 30-day window without waiting.</summary>
    public static async Task SetDeletedAtAsync(PostgresFixture db, Guid itemId, DateTimeOffset deletedAt)
    {
        await using var context = db.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"update items set deleted_at = {deletedAt} where id = {itemId}");
    }

    public static async Task<int> CountItemsAsync(PostgresFixture db, Guid libraryId)
    {
        await using var context = db.CreateContext();
        return await context.Items.IgnoreQueryFilters().CountAsync(i => i.LibraryId == libraryId);
    }

    public static async Task<int> CountReleasesAsync(PostgresFixture db)
    {
        await using var context = db.CreateContext();
        return await context.Releases.IgnoreQueryFilters().CountAsync();
    }
}
