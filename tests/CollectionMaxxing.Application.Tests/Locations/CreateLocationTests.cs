using CollectionMaxxing.Application.Locations;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Locations;

public class CreateLocationTests(PostgresFixture db) : DatabaseTest(db)
{
    private async Task<(TestLibrary Library, CreateLocation.Handler Handler)> ArrangeAsync()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var handler = new CreateLocation.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Editor)), new FakeIdGenerator());
        return (library, handler);
    }

    [Fact]
    public async Task A_new_location_is_saved_with_a_generated_id()
    {
        var (library, handler) = await ArrangeAsync();

        var result = await handler.HandleAsync(new CreateLocation.Command(library.Id, null, "Living room", null), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new LocationDto(new Guid(1, 0, 0, new byte[8]), null, "Living room", 0));
        var stored = await TestData.ReadLocationsAsync(Db, library.Id);
        stored.Select(l => l.Name).ShouldBe(["Living room"]);
    }

    [Fact]
    public async Task A_location_can_be_created_inside_another()
    {
        var (library, handler) = await ArrangeAsync();
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");

        var result = await handler.HandleAsync(new CreateLocation.Command(library.Id, room, "Shelf", null), CancellationToken.None);

        result.Value.ParentId.ShouldBe(room);
    }

    [Fact]
    public async Task A_blank_name_is_rejected_and_nothing_is_saved()
    {
        var (library, handler) = await ArrangeAsync();

        var result = await handler.HandleAsync(new CreateLocation.Command(library.Id, null, "  ", null), CancellationToken.None);

        result.Error!.Code.ShouldBe("location.name_required");
        (await TestData.ReadLocationsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_name_already_used_by_a_sibling_is_a_conflict()
    {
        var (library, handler) = await ArrangeAsync();
        await TestData.CreateLocationAsync(Db, library.Id, "Garage");

        var result = await handler.HandleAsync(new CreateLocation.Command(library.Id, null, "garage", null), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Code.ShouldBe("location.name_duplicate");
    }

    [Fact]
    public async Task The_parent_must_be_in_the_same_library()
    {
        var (library, handler) = await ArrangeAsync();
        var otherLibrary = await TestData.CreateLibraryAsync(Db);
        var foreignRoom = await TestData.CreateLocationAsync(Db, otherLibrary.Id, "Their room");

        var result = await handler.HandleAsync(new CreateLocation.Command(library.Id, foreignRoom, "Shelf", null), CancellationToken.None);

        result.Error!.Code.ShouldBe("location.parent_not_found");
        (await TestData.ReadLocationsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_can_be_added_to_a_library_the_caller_does_not_belong_to()
    {
        // The API has already refused such a request by the time a handler runs. This is the second lock.
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var handler = new CreateLocation.Handler(Db.CreateContext(mine.ActingAs(LibraryRole.Editor)), new FakeIdGenerator());

        var result = await handler.HandleAsync(new CreateLocation.Command(theirs.Id, null, "Sneaky", null), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        result.Error.Code.ShouldBe("library.not_found");
        (await TestData.ReadLocationsAsync(Db, theirs.Id)).ShouldBeEmpty();
    }
}
