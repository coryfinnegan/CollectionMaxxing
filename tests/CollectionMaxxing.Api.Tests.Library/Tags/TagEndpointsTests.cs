using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Tags;

/// <summary>
/// Acceptance tests for the tags API, from the caller's side. Who may call what is covered by the
/// authorization matrix; these tests are about behaviour.
/// </summary>
public class TagEndpointsTests(ApiFixture api) : ApiTest(api)
{
    [Fact]
    public async Task An_editor_creates_a_tag_with_its_name_trimmed()
    {
        var library = await CreateLibraryAsync();

        var created = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync(TagsUrl(library.Id), new { name = "  Sci-Fi  " });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await ReadJsonAsync(created);
        body.GetProperty("name").GetString().ShouldBe("Sci-Fi");
        body.GetProperty("itemCount").GetInt32().ShouldBe(0);
        created.Headers.Location!.ToString().ShouldBe(TagUrl(library.Id, body.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task A_blank_name_is_a_validation_problem_that_names_the_field()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync(TagsUrl(library.Id), new { name = "   " });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("errors").GetProperty("name").EnumerateArray().Count().ShouldBe(1);
        problem.GetProperty("code").GetString().ShouldBe("tag.name_required");
    }

    [Fact]
    public async Task A_missing_name_is_the_same_validation_problem()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor).PostAsJsonAsync(TagsUrl(library.Id), new { });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("tag.name_required");
    }

    [Fact]
    public async Task A_name_of_51_characters_is_rejected_and_50_is_fine()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);

        var tooLong = await editor.PostAsJsonAsync(TagsUrl(library.Id), new { name = new string('x', 51) });
        var justRight = await editor.PostAsJsonAsync(TagsUrl(library.Id), new { name = new string('x', 50) });

        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(tooLong)).GetProperty("code").GetString().ShouldBe("tag.name_too_long");
        justRight.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_name_with_a_control_character_is_rejected()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync(TagsUrl(library.Id), new { name = "Sci\u0007Fi" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("tag.name_invalid");
    }

    [Fact]
    public async Task Sci_fi_then_sci_fi_in_lower_case_in_the_same_library_is_a_conflict()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        (await editor.PostAsJsonAsync(TagsUrl(library.Id), new { name = "Sci-Fi" })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await editor.PostAsJsonAsync(TagsUrl(library.Id), new { name = "sci-fi" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await ReadJsonAsync(response)).GetProperty("status").GetInt32().ShouldBe(409);
    }

    [Fact]
    public async Task The_same_name_in_a_different_library_is_fine()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        await TestData.CreateTagAsync(Db, theirs.Id, "Sci-Fi");

        var response = await ClientFor(mine, LibraryRole.Editor).PostAsJsonAsync(TagsUrl(mine.Id), new { name = "Sci-Fi" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Listing_is_sorted_case_insensitively_and_holds_only_this_librarys_tags()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        await TestData.CreateTagAsync(Db, mine.Id, "vinyl");
        await TestData.CreateTagAsync(Db, mine.Id, "Books");
        await TestData.CreateTagAsync(Db, mine.Id, "Comics");
        await TestData.CreateTagAsync(Db, theirs.Id, "Aardvarks");

        var list = await ReadJsonAsync(await ClientFor(mine, LibraryRole.Viewer).GetAsync(TagsUrl(mine.Id)));

        list.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .ShouldBe(["Books", "Comics", "vinyl"]);
        list.GetProperty("nextCursor").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task A_tags_item_count_leaves_out_items_in_the_trash_and_counts_them_again_once_restored()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var tag = await TestData.CreateTagAsync(Db, library.Id, "Vinyl");
        var kept = await TestData.CreateItemAsync(Db, library);
        var trashed = await TestData.CreateItemAsync(Db, library);
        await TestData.AttachTagAsync(Db, library.Id, kept, tag);
        await TestData.AttachTagAsync(Db, library.Id, trashed, tag);
        var trashedUrl = $"/api/v1/libraries/{library.Id}/items/{trashed}";
        using var delete = new HttpRequestMessage(HttpMethod.Delete, trashedUrl);
        delete.Headers.TryAddWithoutValidation("If-Match", (await editor.GetAsync(trashedUrl)).Headers.ETag!.Tag);
        (await editor.SendAsync(delete)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ItemCountOfAsync(editor, library.Id)).ShouldBe(1);

        (await editor.PostAsync($"{trashedUrl}:restore", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ItemCountOfAsync(editor, library.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task Deleting_a_tag_removes_it_and_its_item_links()
    {
        var library = await CreateLibraryAsync();
        var tag = await TestData.CreateTagAsync(Db, library.Id, "Vinyl");
        var item = await TestData.CreateItemAsync(Db, library);
        await TestData.AttachTagAsync(Db, library.Id, item, tag);

        var response = await ClientFor(library, LibraryRole.Editor).DeleteAsync(TagUrl(library.Id, tag));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await TestData.ReadTagNamesAsync(Db, library.Id)).ShouldBeEmpty();
        (await TestData.CountItemTagsAsync(Db, library.Id)).ShouldBe(0);
    }

    // ---- tenant isolation (IDOR) --------------------------------------------------------------

    [Fact]
    public async Task Deleting_another_librarys_tag_is_404_and_changes_nothing()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var theirTag = await TestData.CreateTagAsync(Db, theirs.Id, "Their tag");

        var response = await ClientFor(mine, LibraryRole.Owner).DeleteAsync(TagUrl(mine.Id, theirTag));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await TestData.ReadTagNamesAsync(Db, theirs.Id)).ShouldBe(["Their tag"]);
    }

    [Fact]
    public async Task Deleting_an_unknown_tag_is_404()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor).DeleteAsync(TagUrl(library.Id, Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>The <c>itemCount</c> of the library's only tag.</summary>
    private static async Task<int> ItemCountOfAsync(HttpClient client, Guid libraryId)
    {
        var list = await ReadJsonAsync(await client.GetAsync(TagsUrl(libraryId)));
        return list.GetProperty("items").EnumerateArray().Single().GetProperty("itemCount").GetInt32();
    }

    private static string TagsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/tags";

    private static string TagUrl(Guid libraryId, Guid tagId) => $"{TagsUrl(libraryId)}/{tagId}";
}
