using CollectionMaxxing.Application.Locations;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Locations;

public class UpdateLocationTests(PostgresFixture db) : DatabaseTest(db)
{
    private UpdateLocation.Handler HandlerFor(TestLibrary library) =>
        new(Db.CreateContext(library.ActingAs(LibraryRole.Editor)));

    [Fact]
    public async Task A_location_can_be_renamed()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");

        var result = await HandlerFor(library).HandleAsync(
            new UpdateLocation.Command(library.Id, shelf, new LocationPatch(Name: "Top shelf")), CancellationToken.None);

        result.Value.Name.ShouldBe("Top shelf");
        (await TestData.ReadLocationsAsync(Db, library.Id)).Single().Name.ShouldBe("Top shelf");
    }

    [Fact]
    public async Task A_location_can_be_moved_inside_another_and_back_to_the_top_level()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");

        await HandlerFor(library).HandleAsync(
            new UpdateLocation.Command(library.Id, shelf, new LocationPatch(ParentSpecified: true, ParentId: room)), CancellationToken.None);
        (await TestData.ReadLocationsAsync(Db, library.Id)).Single(l => l.Id == shelf).ParentId.ShouldBe(room);

        await HandlerFor(library).HandleAsync(
            new UpdateLocation.Command(library.Id, shelf, new LocationPatch(ParentSpecified: true, ParentId: null)), CancellationToken.None);
        (await TestData.ReadLocationsAsync(Db, library.Id)).Single(l => l.Id == shelf).ParentId.ShouldBeNull();
    }

    [Fact]
    public async Task Moving_a_location_inside_its_own_descendant_is_a_conflict_and_changes_nothing()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf", room);

        var result = await HandlerFor(library).HandleAsync(
            new UpdateLocation.Command(library.Id, room, new LocationPatch(ParentSpecified: true, ParentId: shelf)), CancellationToken.None);

        result.Error!.Code.ShouldBe("location.cycle");
        (await TestData.ReadLocationsAsync(Db, library.Id)).Single(l => l.Id == room).ParentId.ShouldBeNull();
    }

    [Fact]
    public async Task Renaming_to_a_siblings_name_is_a_conflict()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, library.Id, "A");
        var b = await TestData.CreateLocationAsync(Db, library.Id, "B");

        var result = await HandlerFor(library).HandleAsync(
            new UpdateLocation.Command(library.Id, b, new LocationPatch(Name: "A")), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task An_unknown_location_is_not_found()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        var result = await HandlerFor(library).HandleAsync(
            new UpdateLocation.Command(library.Id, Guid.CreateVersion7(), new LocationPatch(Name: "x")), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_location_in_another_library_is_not_found_even_with_its_real_id()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var theirShelf = await TestData.CreateLocationAsync(Db, theirs.Id, "Their shelf");

        var result = await HandlerFor(mine).HandleAsync(
            new UpdateLocation.Command(mine.Id, theirShelf, new LocationPatch(Name: "Mine now")), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await TestData.ReadLocationsAsync(Db, theirs.Id)).Single().Name.ShouldBe("Their shelf");
    }
}
