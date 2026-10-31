using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Catalog;

/// <summary>
/// Adding an item from a catalogue search result that only a provider knows. Every such result carries the empty id, so the
/// add names the hit by the provider and its own id instead, and the server saves the work and release as it adds.
/// </summary>
public class AddFromSearchTests(ApiFixture api) : CatalogTest(api)
{
    private static string ItemsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/items";

    private static FakeProvider ThreeDoomGames()
    {
        var provider = new FakeProvider("igdb");
        provider.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "doom-1993", WorkKey = "1", Title = "Doom", Kind = WorkKind.Game, Platform = "PC (Microsoft Windows)", Year = 1993, Creators = ["id Software"], Publisher = "id Software" });
        provider.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "doom-ii-1994", WorkKey = "2", Title = "Doom II", Kind = WorkKind.Game, Platform = "PC (Microsoft Windows)", Year = 1994, Creators = ["id Software"], Publisher = "GT Interactive", Synopsis = "Hell on Earth." });
        provider.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "doom-3-2004", WorkKey = "3", Title = "Doom 3", Kind = WorkKind.Game, Platform = "Xbox", Year = 2004, Creators = ["id Software"], Publisher = "Activision" });
        return provider;
    }

    private static async Task<List<JsonElement>> SearchAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/api/v1/catalog/search?" + query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
    }

    private static JsonElement ResultTitled(IEnumerable<JsonElement> results, string title) =>
        results.Single(r => r.GetProperty("work").GetProperty("title").GetString() == title);

    /// <summary>The provider reference the search carried on a result's first release, ready to send back as <c>providerRef</c>.</summary>
    private static object RefOf(JsonElement result)
    {
        var reference = result.GetProperty("releases")[0].GetProperty("providerRef");
        return new { provider = reference.GetProperty("provider").GetString(), externalId = reference.GetProperty("externalId").GetString() };
    }

    private static async Task<HttpResponseMessage> AddAsync(HttpClient client, Guid libraryId, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl(libraryId)) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid libraryId, JsonElement created) =>
        await ReadJsonAsync(await client.GetAsync($"{ItemsUrl(libraryId)}/{created.GetProperty("id").GetGuid()}"));

    [Fact]
    public async Task Search_names_each_provider_hit_so_it_can_be_added()
    {
        var library = await CreateLibraryAsync();
        var client = ClientWith(library.EditorId, ThreeDoomGames());

        var results = await SearchAsync(client, "q=doom");

        results.Count.ShouldBe(3);
        results.SelectMany(r => r.GetProperty("releases").EnumerateArray())
            .Select(release => release.GetProperty("providerRef").GetProperty("externalId").GetString())
            .Order().ShouldBe(["doom-1993", "doom-3-2004", "doom-ii-1994"]);
        results.SelectMany(r => r.GetProperty("releases").EnumerateArray())
            .Select(release => release.GetProperty("providerRef").GetProperty("provider").GetString()).Distinct().ShouldBe(["igdb"]);
    }

    [Fact]
    public async Task A_release_the_catalogue_already_has_carries_no_provider_ref()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Doom", WorkKind.Game, 1993);
        var client = ClientWith(library.EditorId);

        var results = await SearchAsync(client, "q=doom");

        var release = results.Single().GetProperty("releases")[0];
        release.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        release.GetProperty("providerRef").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Adding_the_second_of_three_provider_hits_creates_the_item_on_that_works_own_release()
    {
        var library = await CreateLibraryAsync();
        var client = ClientWith(library.EditorId, ThreeDoomGames());
        var results = await SearchAsync(client, "q=doom");
        var second = ResultTitled(results, "Doom II");

        var response = await AddAsync(client, library.Id, new { providerRef = RefOf(second), addedVia = "search" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = await ReadJsonAsync(response);
        created.GetProperty("addedVia").GetString().ShouldBe("search");
        var detail = await DetailAsync(client, library.Id, created);
        detail.GetProperty("work").GetProperty("title").GetString().ShouldBe("Doom II");
        detail.GetProperty("work").GetProperty("originalYear").GetInt32().ShouldBe(1994);
        detail.GetProperty("work").GetProperty("synopsis").GetString().ShouldBe("Hell on Earth.");
        var release = detail.GetProperty("release");
        release.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        release.GetProperty("source").GetString().ShouldBe("provider");
        release.GetProperty("publisher").GetString().ShouldBe("GT Interactive");
        await using var context = Db.CreateContext();
        (await context.Works.AsNoTracking().Select(w => w.Title).ToListAsync()).ShouldBe(["Doom II"]);
    }

    [Fact]
    public async Task Adding_each_of_three_provider_hits_gives_each_its_own_work()
    {
        var library = await CreateLibraryAsync();
        var client = ClientWith(library.EditorId, ThreeDoomGames());
        var results = await SearchAsync(client, "q=doom");

        foreach (var result in results)
        {
            (await AddAsync(client, library.Id, new { providerRef = RefOf(result) })).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        await using var context = Db.CreateContext();
        (await context.Works.AsNoTracking().Select(w => w.Title).OrderBy(t => t).ToListAsync()).ShouldBe(["Doom", "Doom 3", "Doom II"]);
        (await context.Releases.AsNoTracking().CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Adding_the_same_hit_twice_gives_two_copies_of_one_release_and_never_a_second_work()
    {
        var library = await CreateLibraryAsync();
        var client = ClientWith(library.EditorId, ThreeDoomGames());
        var providerRef = RefOf(ResultTitled(await SearchAsync(client, "q=doom"), "Doom II"));

        var first = await ReadJsonAsync(await AddAsync(client, library.Id, new { providerRef }));
        var second = await ReadJsonAsync(await AddAsync(client, library.Id, new { providerRef }));

        second.GetProperty("id").GetGuid().ShouldNotBe(first.GetProperty("id").GetGuid());
        second.GetProperty("releaseId").GetGuid().ShouldBe(first.GetProperty("releaseId").GetGuid());
        await using var context = Db.CreateContext();
        (await context.Works.AsNoTracking().CountAsync()).ShouldBe(1);
        (await context.Releases.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Replaying_the_same_provider_add_with_the_same_key_returns_the_same_item()
    {
        var library = await CreateLibraryAsync();
        var client = ClientWith(library.EditorId, ThreeDoomGames());
        var providerRef = RefOf(ResultTitled(await SearchAsync(client, "q=doom"), "Doom"));

        var first = await ReadJsonAsync(await AddAsync(client, library.Id, new { providerRef }, "one-key"));
        var replay = await ReadJsonAsync(await AddAsync(client, library.Id, new { providerRef }, "one-key"));

        replay.GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());
        await using var context = Db.CreateContext();
        (await context.Items.IgnoreQueryFilters().AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_hit_added_from_another_librarys_search_is_a_shared_catalogue_release_not_a_private_one()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var provider = ThreeDoomGames();
        var providerRef = RefOf(ResultTitled(await SearchAsync(ClientWith(mine.EditorId, provider), "q=doom"), "Doom II"));

        // The search was mine; the add may come from any editor who was shown the hit (the stash is the provider's data, not mine).
        var added = await AddAsync(ClientWith(theirs.EditorId, provider), theirs.Id, new { providerRef });

        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        (await context.Releases.IgnoreQueryFilters().AsNoTracking().Select(r => r.OwnerLibraryId).ToListAsync()).ShouldBe([null]);
    }

    [Fact]
    public async Task A_provider_key_the_server_never_showed_is_not_found_with_a_friendly_code()
    {
        var library = await CreateLibraryAsync();
        var client = ClientWith(library.EditorId, ThreeDoomGames());

        var response = await AddAsync(client, library.Id, new { providerRef = new { provider = "igdb", externalId = "made-up" } });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("provider_hit.not_found");
        problem.GetProperty("detail").GetString()!.ShouldContain("Search again");
        await using var context = Db.CreateContext();
        (await context.Items.IgnoreQueryFilters().AsNoTracking().CountAsync()).ShouldBe(0);
        (await context.Works.AsNoTracking().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("igdb", "")]
    [InlineData(" ", " ")]
    public async Task A_provider_ref_missing_its_provider_or_id_is_a_validation_problem(string provider, string externalId)
    {
        var library = await CreateLibraryAsync();
        var client = ClientWith(library.EditorId, ThreeDoomGames());

        var response = await AddAsync(client, library.Id, new { providerRef = new { provider, externalId } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.provider_ref_invalid");
    }

    [Fact]
    public async Task A_provider_ref_beside_a_release_id_or_manual_details_is_rejected()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var client = ClientWith(library.EditorId, ThreeDoomGames());
        var providerRef = RefOf(ResultTitled(await SearchAsync(client, "q=doom"), "Doom"));

        var withRelease = await AddAsync(client, library.Id, new { releaseId, providerRef });
        var withManual = await AddAsync(client, library.Id, new { manual = new { title = "Doom" }, providerRef });

        withRelease.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(withRelease)).GetProperty("code").GetString().ShouldBe("item.source_required");
        withManual.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(withManual)).GetProperty("code").GetString().ShouldBe("item.source_required");
    }

    [Fact]
    public async Task A_viewer_cannot_add_a_provider_hit()
    {
        var library = await CreateLibraryAsync();
        var client = ClientWith(library.ViewerId, ThreeDoomGames());
        var providerRef = RefOf(ResultTitled(await SearchAsync(client, "q=doom"), "Doom"));

        var response = await AddAsync(client, library.Id, new { providerRef });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
