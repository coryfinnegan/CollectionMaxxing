using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Items;

/// <summary>Acceptance tests for <c>POST …/items:bulk</c> as the web table's selection bar calls it.</summary>
public class BulkItemsEndpointTests(ApiFixture api) : ApiTest(api)
{
    private static string BulkUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/items:bulk";

    private static string ItemUrl(Guid libraryId, Guid itemId) => $"/api/v1/libraries/{libraryId}/items/{itemId}";

    /// <summary>Posts a bulk patch with <paramref name="patchJson"/> as written, so a test can leave a property out or send it as <c>null</c>.</summary>
    private static async Task<HttpResponseMessage> BulkPatchAsync(HttpClient client, Guid libraryId, Guid[] ids, string patchJson)
    {
        var body = new JsonObject { ["ids"] = JsonSerializer.SerializeToNode(ids), ["action"] = "patch", ["patch"] = JsonNode.Parse(patchJson) };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await client.PostAsync(BulkUrl(libraryId), content);
    }

    private static async Task<JsonElement> ItemAsync(HttpClient client, Guid libraryId, Guid itemId) =>
        (await ReadJsonAsync(await client.GetAsync(ItemUrl(libraryId, itemId)))).GetProperty("item");

    [Fact]
    public async Task Moving_items_to_no_location_clears_the_location_of_every_one()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var first = await TestData.CreateItemAsync(Db, library, locationId: shelf);
        var second = await TestData.CreateItemAsync(Db, library, locationId: shelf);

        var response = await BulkPatchAsync(editor, library.Id, [first, second], """{"locationId":null}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("affected").GetInt32().ShouldBe(2);
        (await ItemAsync(editor, library.Id, first)).GetProperty("locationId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await ItemAsync(editor, library.Id, second)).GetProperty("locationId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Clearing_the_location_while_changing_the_status_does_both()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var item = await TestData.CreateItemAsync(Db, library, locationId: shelf);

        var response = await BulkPatchAsync(editor, library.Id, [item], """{"locationId":null,"status":"missing"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await ItemAsync(editor, library.Id, item);
        after.GetProperty("locationId").ValueKind.ShouldBe(JsonValueKind.Null);
        after.GetProperty("status").GetString().ShouldBe("missing");
    }

    [Fact]
    public async Task A_status_change_that_leaves_out_the_location_keeps_the_location()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var item = await TestData.CreateItemAsync(Db, library, locationId: shelf);

        var response = await BulkPatchAsync(editor, library.Id, [item], """{"status":"wishlist"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await ItemAsync(editor, library.Id, item);
        after.GetProperty("locationId").GetGuid().ShouldBe(shelf);
        after.GetProperty("status").GetString().ShouldBe("wishlist");
    }

    [Fact]
    public async Task Moving_items_to_a_location_puts_them_there()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var item = await TestData.CreateItemAsync(Db, library);

        var response = await BulkPatchAsync(editor, library.Id, [item], $$"""{"locationId":"{{shelf}}"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ItemAsync(editor, library.Id, item)).GetProperty("locationId").GetGuid().ShouldBe(shelf);
    }

    [Fact]
    public async Task A_patch_that_names_nothing_to_change_is_still_refused()
    {
        var library = await CreateLibraryAsync();
        var item = await TestData.CreateItemAsync(Db, library);

        var response = await BulkPatchAsync(ClientFor(library, LibraryRole.Editor), library.Id, [item], """{}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("bulk.patch_required");
    }

    [Fact]
    public async Task Moving_items_to_a_location_that_is_not_in_the_library_is_refused_and_changes_nothing()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var elsewhere = await TestData.CreateLocationAsync(Db, other.Id, "Elsewhere");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var item = await TestData.CreateItemAsync(Db, library, locationId: shelf);
        var editor = ClientFor(library, LibraryRole.Editor);

        var response = await BulkPatchAsync(editor, library.Id, [item], $$"""{"locationId":"{{elsewhere}}"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ItemAsync(editor, library.Id, item)).GetProperty("locationId").GetGuid().ShouldBe(shelf);
    }

    [Fact]
    public async Task Setting_a_status_away_from_lent_clears_the_borrower_of_every_item()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var item = await TestData.CreateItemAsync(Db, library);
        await LendAsync(item, "Sam");

        var response = await BulkPatchAsync(editor, library.Id, [item], """{"status":"owned"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await ItemAsync(editor, library.Id, item);
        after.GetProperty("status").GetString().ShouldBe("owned");
        after.GetProperty("lentTo").ValueKind.ShouldBe(JsonValueKind.Null);
        after.GetProperty("lentOn").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Setting_lent_needs_a_borrower_so_a_bulk_call_that_would_lend_an_unlent_item_is_refused_whole()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var borrowed = await TestData.CreateItemAsync(Db, library);
        await LendAsync(borrowed, "Sam");
        var owned = await TestData.CreateItemAsync(Db, library);
        var tag = await TestData.CreateTagAsync(Db, library.Id, "signed");

        var response = await BulkPatchAsync(editor, library.Id, [borrowed, owned], $$"""{"status":"lent","addTagIds":["{{tag}}"]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("item.lent_to_required");
        problem.GetProperty("errors").TryGetProperty("status", out _).ShouldBeTrue();
        (await ItemAsync(editor, library.Id, owned)).GetProperty("status").GetString().ShouldBe("owned");
        (await TestData.CountItemTagsAsync(Db, library.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task Setting_lent_on_items_that_already_have_a_borrower_changes_nothing_about_the_lend()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var item = await TestData.CreateItemAsync(Db, library);
        await LendAsync(item, "Sam");

        var response = await BulkPatchAsync(editor, library.Id, [item], """{"status":"lent"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await ItemAsync(editor, library.Id, item);
        after.GetProperty("status").GetString().ShouldBe("lent");
        after.GetProperty("lentTo").GetString().ShouldBe("Sam");
        after.GetProperty("lentOn").GetString().ShouldBe("2026-05-01");
    }

    private async Task LendAsync(Guid itemId, string to)
    {
        await using var context = Db.CreateContext();
        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == itemId);
        item.Status = ItemStatus.Lent;
        item.LentTo = to;
        item.LentOn = new DateOnly(2026, 5, 1);
        await context.SaveChangesAsync();
    }
}
