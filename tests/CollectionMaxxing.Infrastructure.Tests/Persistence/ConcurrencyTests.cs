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
