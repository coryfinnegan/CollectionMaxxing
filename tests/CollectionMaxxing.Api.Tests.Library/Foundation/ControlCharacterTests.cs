using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Foundation;

/// <summary>
/// Postgres can't store a NUL in text or jsonb. Free text with a control character in it is the caller's mistake: a 400 that names the
/// field, never a 500. Tab and line breaks are fine where people type them (notes).
/// </summary>
public class ControlCharacterTests(ApiFixture api) : ApiTest(api)
{
    private const string Code = "text.invalid_character";

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string field)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe(Code);
        problem.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue($"the problem should name {field}");
    }

    private static async Task<HttpResponseMessage> PatchItemAsync(HttpClient client, string url, string json)
    {
        var etag = (await client.GetAsync(url)).Headers.ETag!.Tag;
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("customTitle")]
    [InlineData("lentTo")]
    public async Task A_nul_in_an_item_edit_is_a_400_that_names_the_field(string field)
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);
        var url = $"/api/v1/libraries/{library.Id}/items/{itemId}";

        var response = await PatchItemAsync(ClientFor(library, LibraryRole.Editor), url, JsonSerializer.Serialize(new Dictionary<string, string> { [field] = "a\u0000b" }));

        await AssertRefusedAsync(response, field);
    }

    [Fact]
    public async Task Tabs_and_line_breaks_are_fine_in_notes()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);
        var url = $"/api/v1/libraries/{library.Id}/items/{itemId}";

        var response = await PatchItemAsync(ClientFor(library, LibraryRole.Editor), url, JsonSerializer.Serialize(new { notes = "one\ttwo\r\nthree" }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("notes").GetString().ShouldBe("one\ttwo\r\nthree");
    }

    [Fact]
    public async Task A_nul_in_the_item_search_text_is_a_400_that_names_the_field()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Viewer).GetAsync($"/api/v1/libraries/{library.Id}/items?q=a%00b");

        await AssertRefusedAsync(response, "q");
    }

    [Fact]
    public async Task A_nul_in_the_catalogue_search_text_is_a_400_that_names_the_field()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Viewer).GetAsync("/api/v1/catalog/search?q=a%00b");

        await AssertRefusedAsync(response, "q");
    }

    [Fact]
    public async Task A_nul_in_a_new_librarys_name_is_a_400_that_names_the_field()
    {
        var userId = await TestData.CreateUserAsync(Db);

        var response = await Api.CreateClient(userId).PostAsJsonAsync("/api/v1/libraries", new { name = "Home\u0000" });

        await AssertRefusedAsync(response, "name");
    }

    [Fact]
    public async Task A_nul_in_a_location_name_is_a_400_that_names_the_field()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor).PostAsJsonAsync(LocationsUrl(library.Id), new { name = "Shelf\u0000A" });

        await AssertRefusedAsync(response, "name");
    }

    [Theory]
    [InlineData("title", "Du\u0000ne")]
    [InlineData("creators", "Frank\u0000Herbert")]
    public async Task A_nul_in_a_manual_item_is_a_400_that_names_the_field(string property, string value)
    {
        var library = await CreateLibraryAsync();
        var manual = new Dictionary<string, object> { ["kind"] = "book", ["title"] = "Dune" };
        manual[property] = property == "creators" ? new[] { value } : value;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/libraries/{library.Id}/items") { Content = JsonContent.Create(new { manual }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await ClientFor(library, LibraryRole.Editor).SendAsync(request);

        await AssertRefusedAsync(response, "manual." + property);
    }

    [Fact]
    public async Task A_nul_in_a_note_on_a_new_item_is_a_400_that_names_the_field()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/libraries/{library.Id}/items") { Content = JsonContent.Create(new { releaseId, notes = "a\u0000b" }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await ClientFor(library, LibraryRole.Editor).SendAsync(request);

        await AssertRefusedAsync(response, "notes");
    }
}
