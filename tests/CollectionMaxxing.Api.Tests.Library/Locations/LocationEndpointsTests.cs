using System.Net;
using System.Net.Http.Json;
using System.Text;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Locations;

/// <summary>
/// Acceptance tests for the locations API, from the caller's side. Who may call what is covered by the
/// authorization matrix; these tests are about behaviour.
/// </summary>
public class LocationEndpointsTests(ApiFixture api) : ApiTest(api)
{
    [Fact]
    public async Task An_editor_creates_a_location_and_everyone_in_the_library_then_sees_it()
    {
        var library = await CreateLibraryAsync();

        var created = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync(LocationsUrl(library.Id), new { name = "Living room" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await ReadJsonAsync(created);
        var id = body.GetProperty("id").GetGuid();
        body.GetProperty("name").GetString().ShouldBe("Living room");
        body.GetProperty("parentId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        created.Headers.Location!.ToString().ShouldBe(LocationUrl(library.Id, id));

        var list = await ReadJsonAsync(await ClientFor(library, LibraryRole.Viewer).GetAsync(LocationsUrl(library.Id)));
        list.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("name").GetString()).ShouldBe(["Living room"]);
        list.GetProperty("nextCursor").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Locations_nest_through_parent_id()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var room = await ReadJsonAsync(await editor.PostAsJsonAsync(LocationsUrl(library.Id), new { name = "Room" }));
        var roomId = room.GetProperty("id").GetGuid();

        var shelf = await editor.PostAsJsonAsync(LocationsUrl(library.Id), new { name = "Shelf", parentId = roomId });

        (await ReadJsonAsync(shelf)).GetProperty("parentId").GetGuid().ShouldBe(roomId);
    }

    [Fact]
    public async Task A_blank_name_is_a_validation_problem_that_names_the_field()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync(LocationsUrl(library.Id), new { name = "  " });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("status").GetInt32().ShouldBe(400);
        problem.GetProperty("errors").GetProperty("name").EnumerateArray().Count().ShouldBe(1);
        problem.GetProperty("code").GetString().ShouldBe("location.name_required");
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_missing_name_is_the_same_validation_problem()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync(LocationsUrl(library.Id), new { });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("location.name_required");
    }

    [Fact]
    public async Task A_name_over_100_characters_is_rejected()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync(LocationsUrl(library.Id), new { name = new string('x', 101) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("location.name_too_long");
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_bad_request_not_a_server_error()
    {
        var library = await CreateLibraryAsync();
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        var response = await ClientFor(library, LibraryRole.Editor).PostAsync(LocationsUrl(library.Id), content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Two_siblings_with_the_same_name_is_a_conflict()
    {
        var library = await CreateLibraryAsync();
        await TestData.CreateLocationAsync(Db, library.Id, "Garage");

        var response = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync(LocationsUrl(library.Id), new { name = "garage" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("location.name_duplicate");
        problem.GetProperty("status").GetInt32().ShouldBe(409);
    }

    [Fact]
    public async Task A_patch_renames_a_location_and_leaves_its_parent_alone()
    {
        var library = await CreateLibraryAsync();
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf", room);

        var response = await ClientFor(library, LibraryRole.Editor)
            .PatchAsJsonAsync(LocationUrl(library.Id, shelf), new { name = "Top shelf" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("name").GetString().ShouldBe("Top shelf");
        body.GetProperty("parentId").GetGuid().ShouldBe(room);
    }

    [Fact]
    public async Task A_patch_with_parent_id_null_moves_a_location_to_the_top_level()
    {
        var library = await CreateLibraryAsync();
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf", room);

        var response = await ClientFor(library, LibraryRole.Editor)
            .PatchAsync(LocationUrl(library.Id, shelf), MergePatch("""{ "parentId": null }"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("parentId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task A_patch_that_would_create_a_cycle_is_a_conflict()
    {
        var library = await CreateLibraryAsync();
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf", room);

        var response = await ClientFor(library, LibraryRole.Editor)
            .PatchAsJsonAsync(LocationUrl(library.Id, room), new { parentId = shelf });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("location.cycle");
    }

    [Fact]
    public async Task Patching_a_location_that_does_not_exist_is_404()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor)
            .PatchAsJsonAsync(LocationUrl(library.Id, Guid.CreateVersion7()), new { name = "x" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_editor_deletes_an_empty_location()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");

        var response = await ClientFor(library, LibraryRole.Editor).DeleteAsync(LocationUrl(library.Id, shelf));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await TestData.ReadLocationsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_a_location_that_has_children_is_a_conflict()
    {
        var library = await CreateLibraryAsync();
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        await TestData.CreateLocationAsync(Db, library.Id, "Shelf", room);

        var response = await ClientFor(library, LibraryRole.Owner).DeleteAsync(LocationUrl(library.Id, room));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("location.has_children");
    }

    // ---- tenant isolation (IDOR) --------------------------------------------------------------

    [Fact]
    public async Task A_location_of_another_library_is_invisible_even_when_asked_for_through_my_own_library()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var theirShelf = await TestData.CreateLocationAsync(Db, theirs.Id, "Their shelf");
        var owner = ClientFor(mine, LibraryRole.Owner);

        (await owner.PatchAsJsonAsync(LocationUrl(mine.Id, theirShelf), new { name = "Mine now" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.DeleteAsync(LocationUrl(mine.Id, theirShelf))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await TestData.ReadLocationsAsync(Db, theirs.Id)).Single().Name.ShouldBe("Their shelf");
    }

    [Fact]
    public async Task A_parent_from_another_library_is_rejected()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var theirRoom = await TestData.CreateLocationAsync(Db, theirs.Id, "Their room");

        var response = await ClientFor(mine, LibraryRole.Editor)
            .PostAsJsonAsync(LocationsUrl(mine.Id), new { name = "Shelf", parentId = theirRoom });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("location.parent_not_found");
    }

    [Fact]
    public async Task Listing_never_includes_another_librarys_locations()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        await TestData.CreateLocationAsync(Db, mine.Id, "Mine");
        await TestData.CreateLocationAsync(Db, theirs.Id, "Theirs");

        var list = await ReadJsonAsync(await ClientFor(mine, LibraryRole.Viewer).GetAsync(LocationsUrl(mine.Id)));

        list.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("name").GetString()).ShouldBe(["Mine"]);
    }

    private static StringContent MergePatch(string json) => new(json, Encoding.UTF8, "application/merge-patch+json");

    [Fact]
    public async Task Deleting_a_location_that_holds_items_is_a_conflict_not_a_server_error()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        await TestData.CreateItemAsync(Db, library, locationId: shelf);

        var response = await ClientFor(library, LibraryRole.Editor).DeleteAsync(LocationUrl(library.Id, shelf));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await ReadJsonAsync(response)).GetProperty("type").GetString().ShouldBe("urn:collectionmaxxing:problem:conflict.foreign_key");
    }
}
