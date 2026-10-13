using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Libraries;

public class LibraryEndpointsTests(ApiFixture api) : ApiTest(api)
{
    private static string LibraryUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}";

    [Fact]
    public async Task The_list_has_only_the_libraries_the_caller_belongs_to_with_their_role()
    {
        var mine = await TestData.CreateLibraryAsync(Db, "Mine");
        await TestData.CreateLibraryAsync(Db, "Not mine");

        var page = await ReadJsonAsync(await ClientFor(mine, LibraryRole.Editor).GetAsync("/api/v1/libraries"));

        var libraries = page.GetProperty("items").EnumerateArray().ToList();
        libraries.Count.ShouldBe(1);
        libraries[0].GetProperty("id").GetGuid().ShouldBe(mine.Id);
        libraries[0].GetProperty("name").GetString().ShouldBe("Mine");
        libraries[0].GetProperty("role").GetString().ShouldBe("editor");
    }

    [Fact]
    public async Task A_member_reads_the_library_with_their_own_role()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Viewer).GetAsync(LibraryUrl(library.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("id").GetGuid().ShouldBe(library.Id);
        body.GetProperty("role").GetString().ShouldBe("viewer");
    }

    [Fact]
    public async Task The_owner_renames_the_library()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Owner).PatchAsJsonAsync(LibraryUrl(library.Id), new { name = "  Our shelf  " });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("name").GetString().ShouldBe("Our shelf");
        (await ReadJsonAsync(await ClientFor(library, LibraryRole.Viewer).GetAsync(LibraryUrl(library.Id)))).GetProperty("name").GetString().ShouldBe("Our shelf");
    }

    [Fact]
    public async Task An_editor_cannot_rename_the_library()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor).PatchAsJsonAsync(LibraryUrl(library.Id), new { name = "Mine now" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_blank_library_name_is_a_validation_problem()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Owner).PatchAsJsonAsync(LibraryUrl(library.Id), new { name = " " });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("library.name_required");
    }

    [Fact]
    public async Task Someone_outside_the_library_cannot_read_or_rename_it()
    {
        var library = await CreateLibraryAsync();
        var outsider = Api.CreateClient(library.OutsiderId);

        (await outsider.GetAsync(LibraryUrl(library.Id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.PatchAsJsonAsync(LibraryUrl(library.Id), new { name = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
