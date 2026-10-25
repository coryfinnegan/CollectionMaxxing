using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

/// <summary>Two people editing the same row: the second save must fail, not silently overwrite the first.</summary>
public class ConcurrencyTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public async Task A_stale_edit_of_an_item_is_refused()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var itemId = await TestData.CreateItemAsync(Db, library);

        await using var first = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        await using var second = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var firstCopy = await first.Items.SingleAsync(i => i.Id == itemId);
        var secondCopy = await second.Items.SingleAsync(i => i.Id == itemId);

        firstCopy.Notes = "first";
        await first.SaveChangesAsync();
        secondCopy.Notes = "second";

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task The_version_changes_every_time_the_row_does()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var itemId = await TestData.CreateItemAsync(Db, library);

        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var item = await context.Items.SingleAsync(i => i.Id == itemId);
        var before = item.Version;
        item.Notes = "changed";
        await context.SaveChangesAsync();

        item.Version.ShouldNotBe(before);
    }

    [Fact]
    public async Task Concurrent_cover_assignment_on_a_release_resolves_by_last_write()
    {
        // Issue #292: releases deliberately carry no xmin token (see CatalogConfigurations): the only post-creation
        // write is cover assignment, and concurrent writers each store a valid cover, so last-write-wins is benign.
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var firstCover = await CreateCoverAsync("first.jpg");
        var secondCover = await CreateCoverAsync("second.jpg");

        await using var first = Db.CreateContext();
        await using var second = Db.CreateContext();
        var firstCopy = await first.Releases.SingleAsync(r => r.Id == releaseId);
        var secondCopy = await second.Releases.SingleAsync(r => r.Id == releaseId);

        firstCopy.CoverAssetId = firstCover;
        await first.SaveChangesAsync();
        secondCopy.CoverAssetId = secondCover;
        await Should.NotThrowAsync(() => second.SaveChangesAsync());

        await using var check = Db.CreateContext();
        (await check.Releases.SingleAsync(r => r.Id == releaseId)).CoverAssetId.ShouldBe(secondCopy.CoverAssetId);
    }

    private async Task<Guid> CreateCoverAsync(string blobPath)
    {
        var asset = new Asset
        {
            Id = Guid.CreateVersion7(),
            Kind = AssetKind.Cover,
            BlobPath = blobPath,
            ContentType = "image/jpeg",
            Sha256 = Guid.CreateVersion7().ToString("N"),
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        await using var context = Db.CreateContext();
        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        return asset.Id;
    }

    [Fact]
    public async Task A_stale_edit_of_a_location_is_refused()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var locationId = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");

        await using var first = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        await using var second = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var firstCopy = await first.Locations.SingleAsync(l => l.Id == locationId);
        var secondCopy = await second.Locations.SingleAsync(l => l.Id == locationId);

        first.Entry(firstCopy).Property(l => l.SortOrder).CurrentValue = 5;
        await first.SaveChangesAsync();
        second.Entry(secondCopy).Property(l => l.SortOrder).CurrentValue = 9;

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }
}
