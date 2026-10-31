using System.Net;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Items;

/// <summary>An item has a borrower exactly while it is lent out: leaving Lent clears who has it, and Lent needs a name.</summary>
public class LentItemRulesTests(ApiFixture api) : ApiTest(api)
{
    private static string ItemUrl(Guid libraryId, Guid itemId) => $"/api/v1/libraries/{libraryId}/items/{itemId}";

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string url, string json)
    {
        var etag = (await client.GetAsync(url)).Headers.ETag!.Tag;
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private async Task<Guid> LentItemAsync(TestLibrary library, string? lentTo = "Sam")
    {
        var itemId = await TestData.CreateItemAsync(Db, library);
        await using var context = Db.CreateContext();
        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == itemId);
        item.Status = ItemStatus.Lent;
        item.LentTo = lentTo;
        item.LentOn = new DateOnly(2026, 5, 1);
        await context.SaveChangesAsync();
        return itemId;
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("wishlist")]
    [InlineData("sold")]
    [InlineData("missing")]
    public async Task Leaving_lent_clears_who_it_was_lent_to_and_since_when(string status)
    {
        var library = await CreateLibraryAsync();
        var itemId = await LentItemAsync(library);

        var response = await PatchAsync(ClientFor(library, LibraryRole.Editor), ItemUrl(library.Id, itemId), $$"""{"status":"{{status}}"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = await ReadJsonAsync(response);
        item.GetProperty("status").GetString().ShouldBe(status);
        item.GetProperty("lentTo").ValueKind.ShouldBe(JsonValueKind.Null);
        item.GetProperty("lentOn").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_borrower_sent_with_a_status_that_is_not_lent_is_not_kept()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);

        var response = await PatchAsync(ClientFor(library, LibraryRole.Editor), ItemUrl(library.Id, itemId), """{"status":"wishlist","lentTo":"Sam"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = await ReadJsonAsync(response);
        item.GetProperty("status").GetString().ShouldBe("wishlist");
        item.GetProperty("lentTo").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("""{"status":"lent"}""")]
    [InlineData("""{"status":"lent","lentTo":null}""")]
    [InlineData("""{"status":"lent","lentTo":"   "}""")]
    [InlineData("""{"status":"lent","lentTo":null,"lentOn":"2026-06-01"}""")]
    public async Task Lending_an_item_with_no_borrower_is_refused_and_changes_nothing(string body)
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);
        var editor = ClientFor(library, LibraryRole.Editor);

        var response = await PatchAsync(editor, ItemUrl(library.Id, itemId), body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("item.lent_to_required");
        problem.GetProperty("errors").GetProperty("lentTo").GetArrayLength().ShouldBe(1);
        (await ReadJsonAsync(await editor.GetAsync(ItemUrl(library.Id, itemId)))).GetProperty("item").GetProperty("status").GetString().ShouldBe("owned");
    }

    [Fact]
    public async Task Clearing_the_borrower_of_a_lent_item_without_changing_its_status_is_refused()
    {
        var library = await CreateLibraryAsync();
        var itemId = await LentItemAsync(library);

        var response = await PatchAsync(ClientFor(library, LibraryRole.Editor), ItemUrl(library.Id, itemId), """{"lentTo":null}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.lent_to_required");
    }

    [Fact]
    public async Task Lending_an_item_with_a_borrower_starts_the_lend_and_trims_the_name()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);

        var response = await PatchAsync(ClientFor(library, LibraryRole.Editor), ItemUrl(library.Id, itemId), """{"status":"lent","lentTo":"  Sam  "}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = await ReadJsonAsync(response);
        item.GetProperty("status").GetString().ShouldBe("lent");
        item.GetProperty("lentTo").GetString().ShouldBe("Sam");
        item.GetProperty("lentOn").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Naming_a_borrower_alone_lends_the_item()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);

        var response = await PatchAsync(ClientFor(library, LibraryRole.Editor), ItemUrl(library.Id, itemId), """{"lentTo":"Sam"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("status").GetString().ShouldBe("lent");
    }

    [Fact]
    public async Task Changing_who_has_a_lent_item_keeps_the_lend_date()
    {
        var library = await CreateLibraryAsync();
        var itemId = await LentItemAsync(library);

        var response = await PatchAsync(ClientFor(library, LibraryRole.Editor), ItemUrl(library.Id, itemId), """{"lentTo":"Alex"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = await ReadJsonAsync(response);
        item.GetProperty("lentTo").GetString().ShouldBe("Alex");
        item.GetProperty("lentOn").GetString().ShouldBe("2026-05-01");
    }

    [Fact]
    public async Task Editing_something_else_on_an_old_lent_item_that_has_no_borrower_is_not_blocked()
    {
        var library = await CreateLibraryAsync();
        var itemId = await LentItemAsync(library, lentTo: null);

        var response = await PatchAsync(ClientFor(library, LibraryRole.Editor), ItemUrl(library.Id, itemId), """{"notes":"Back soon"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("notes").GetString().ShouldBe("Back soon");
    }
}
