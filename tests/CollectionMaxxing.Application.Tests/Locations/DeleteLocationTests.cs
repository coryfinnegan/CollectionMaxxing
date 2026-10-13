using CollectionMaxxing.Application.Locations;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Locations;

public class DeleteLocationTests(PostgresFixture db) : DatabaseTest(db)
{
    private DeleteLocation.Handler HandlerFor(TestLibrary library) =>
        new(Db.CreateContext(library.ActingAs(LibraryRole.Editor)));

    [Fact]
    public async Task A_location_with_nothing_inside_can_be_deleted()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");

        var result = await HandlerFor(library).HandleAsync(new DeleteLocation.Command(library.Id, shelf), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        (await TestData.ReadLocationsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_location_with_children_is_a_conflict_and_stays()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        await TestData.CreateLocationAsync(Db, library.Id, "Shelf", room);

        var result = await HandlerFor(library).HandleAsync(new DeleteLocation.Command(library.Id, room), CancellationToken.None);

        result.Error!.Code.ShouldBe("location.has_children");
        (await TestData.ReadLocationsAsync(Db, library.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_unknown_location_is_not_found()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        var result = await HandlerFor(library).HandleAsync(new DeleteLocation.Command(library.Id, Guid.CreateVersion7()), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_location_in_another_library_is_not_found_and_survives()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var theirShelf = await TestData.CreateLocationAsync(Db, theirs.Id, "Their shelf");

        var result = await HandlerFor(mine).HandleAsync(new DeleteLocation.Command(mine.Id, theirShelf), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await TestData.ReadLocationsAsync(Db, theirs.Id)).Count.ShouldBe(1);
    }
}
