using CollectionMaxxing.Application.Locations;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Locations;

public class ListLocationsTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public async Task It_returns_the_libraries_locations_with_their_parent_and_order()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf", room);
        var handler = new ListLocations.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)));

        var page = await handler.HandleAsync(new ListLocations.Query(library.Id), CancellationToken.None);

        page.Items.ShouldBe([
            new LocationDto(room, null, "Room", 0),
            new LocationDto(shelf, room, "Shelf", 0),
        ]);
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Siblings_come_back_in_sort_order_then_by_name()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, library.Id, "B", sortOrder: 1);
        await TestData.CreateLocationAsync(Db, library.Id, "A", sortOrder: 1);
        await TestData.CreateLocationAsync(Db, library.Id, "Z", sortOrder: 0);
        var handler = new ListLocations.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)));

        var page = await handler.HandleAsync(new ListLocations.Query(library.Id), CancellationToken.None);

        page.Items.Select(l => l.Name).ShouldBe(["Z", "A", "B"]);
    }

    [Fact]
    public async Task It_never_returns_another_librarys_locations()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, theirs.Id, "Their shelf");
        var handler = new ListLocations.Handler(Db.CreateContext(mine.ActingAs(LibraryRole.Viewer)));

        (await handler.HandleAsync(new ListLocations.Query(mine.Id), CancellationToken.None)).Items.ShouldBeEmpty();
        (await handler.HandleAsync(new ListLocations.Query(theirs.Id), CancellationToken.None)).Items.ShouldBeEmpty();
    }
}
