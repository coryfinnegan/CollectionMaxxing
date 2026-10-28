using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Catalog;

public class CatalogSearchTests(ApiFixture api) : CatalogTest(api)
{
    private async Task<Guid> SomeUserAsync() => await TestData.CreateUserAsync(Db);

    [Fact]
    public async Task Search_ranks_local_matches_by_similarity()
    {
        await CatalogSeed.AddAsync(Db, "Jurassic Park");
        await CatalogSeed.AddAsync(Db, "Jurassic Park III", year: 2001);
        await CatalogSeed.AddAsync(Db, "The Matrix");
        var client = ClientWith(await SomeUserAsync());

        var response = await client.GetAsync("/api/v1/catalog/search?q=jurassic park");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TitlesAsync(response)).ShouldBe(["Jurassic Park", "Jurassic Park III"]);
    }

    [Fact]
    public async Task A_reversed_article_title_finds_the_normal_one()
    {
        await CatalogSeed.AddAsync(Db, "The Matrix");
        var client = ClientWith(await SomeUserAsync());

        (await TitlesAsync(await client.GetAsync("/api/v1/catalog/search?q=" + Uri.EscapeDataString("Matrix, The"))))
            .ShouldBe(["The Matrix"]);
    }

    [Fact]
    public async Task Search_filters_by_kind_platform_and_year()
    {
        var n64 = await CatalogSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        var snes = await CatalogSeed.AddPlatformAsync(Db, "Super Nintendo", "SNES");
        await CatalogSeed.AddAsync(Db, "Mario Kart", WorkKind.Game, 1996, n64, format: ReleaseFormat.GameCartridge);
        await CatalogSeed.AddAsync(Db, "Mario Kart", WorkKind.Game, 1992, snes, format: ReleaseFormat.GameCartridge);
        await CatalogSeed.AddAsync(Db, "Mario Kart", WorkKind.Film, 2001);
        var client = ClientWith(await SomeUserAsync());

        var byKind = await ReadItemsAsync(client, "q=mario kart&kind=game");
        var byPlatform = await ReadItemsAsync(client, "q=mario kart&platform=N64");
        var byYear = await ReadItemsAsync(client, "q=mario kart&year=1992");

        byKind.Count.ShouldBe(2);
        byPlatform.Count.ShouldBe(1);
        byPlatform[0].GetProperty("releases")[0].GetProperty("platform").GetProperty("shortName").GetString().ShouldBe("N64");
        byYear.Count.ShouldBe(1);
        byYear[0].GetProperty("work").GetProperty("originalYear").GetInt32().ShouldBe(1992);
    }

    [Fact]
    public async Task A_blank_query_is_a_validation_problem_on_q()
    {
        var response = await ClientWith(await SomeUserAsync()).GetAsync("/api/v1/catalog/search?q=%20");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("errors").TryGetProperty("q", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Scope_library_ranks_what_you_own_first()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Jurassic Park");
        var (_, ownedReleaseId) = await CatalogSeed.AddAsync(Db, "Jurassic Park III", year: 2001);
        await TestData.CreateItemAsync(Db, library, ownedReleaseId);
        var client = ClientWith(library.ViewerId);

        // Without the scope the closer title wins, owned or not.
        (await TitlesAsync(await client.GetAsync("/api/v1/catalog/search?q=jurassic park")))
            .ShouldBe(["Jurassic Park", "Jurassic Park III"]);

        (await TitlesAsync(await client.GetAsync("/api/v1/catalog/search?q=jurassic park&scope=library")))
            .ShouldBe(["Jurassic Park III", "Jurassic Park"]);
    }

    [Fact]
    public async Task Scope_library_ranks_your_librarys_own_releases_first()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Jurassic Park");
        await CatalogSeed.AddAsync(Db, "Jurassic Park III", year: 2001, ownerLibraryId: library.Id);
        var client = ClientWith(library.ViewerId);

        (await TitlesAsync(await client.GetAsync("/api/v1/catalog/search?q=jurassic park&scope=library")))
            .ShouldBe(["Jurassic Park III", "Jurassic Park"]);
    }

    [Fact]
    public async Task Scope_library_does_not_promote_what_another_library_owns()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Jurassic Park");
        var (_, releaseId) = await CatalogSeed.AddAsync(Db, "Jurassic Park III", year: 2001);
        await TestData.CreateItemAsync(Db, theirs, releaseId);

        var titles = await TitlesAsync(await ClientWith(mine.ViewerId).GetAsync("/api/v1/catalog/search?q=jurassic park&scope=library"));

        titles.ShouldBe(["Jurassic Park", "Jurassic Park III"]);
    }

    [Fact]
    public async Task An_unknown_scope_is_a_validation_problem_on_scope()
    {
        var response = await ClientWith(await SomeUserAsync()).GetAsync("/api/v1/catalog/search?q=alien&scope=galaxy");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("errors").TryGetProperty("scope", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_kind_is_a_validation_problem_on_kind()
    {
        var response = await ClientWith(await SomeUserAsync()).GetAsync("/api/v1/catalog/search?q=alien&kind=podcast");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("errors").TryGetProperty("kind", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_provider_that_throws_does_not_fail_the_search()
    {
        await CatalogSeed.AddAsync(Db, "Alien");
        var broken = new FakeProvider("broken") { Behaviour = _ => throw new InvalidOperationException("boom") };
        var client = ClientWith(await SomeUserAsync(), broken);

        var response = await client.GetAsync("/api/v1/catalog/search?q=alien");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TitlesAsync(response)).ShouldBe(["Alien"]);
    }

    [Fact]
    public async Task Search_and_lookup_ask_the_providers_as_a_person_waiting_so_a_failure_is_not_retried()
    {
        var seen = new List<bool>();
        var provider = new FakeProvider("probe") { Behaviour = _ => { seen.Add(ProviderCalls.IsFailFast); return Task.CompletedTask; } };
        var client = ClientWith(await SomeUserAsync(), provider);

        (await client.GetAsync("/api/v1/catalog/search?q=nothing-here")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/catalog/lookup?code=0045496870010")).StatusCode.ShouldBe(HttpStatusCode.OK);

        seen.Count.ShouldBe(2);
        seen.ShouldAllBe(fast => fast);
        ProviderCalls.IsFailFast.ShouldBeFalse();
    }

    [Fact]
    public async Task A_provider_that_never_answers_does_not_fail_the_search()
    {
        await CatalogSeed.AddAsync(Db, "Alien");
        var hung = new FakeProvider("hung") { Behaviour = _ => Task.Delay(Timeout.Infinite, CancellationToken.None) }; // ignores cancellation
        var client = ClientWith(await SomeUserAsync(), hung);

        var response = await client.GetAsync("/api/v1/catalog/search?q=alien");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TitlesAsync(response)).ShouldBe(["Alien"]);
    }

    [Fact]
    public async Task Provider_results_are_merged_after_the_local_ones_and_do_not_repeat_them()
    {
        await CatalogSeed.AddAsync(Db, "Alien", year: 1979);
        var provider = new FakeProvider("tmdb");
        provider.Results.Add(new ProviderRelease { Provider = "tmdb", ExternalId = "1", Title = "Alien", Kind = WorkKind.Film, Year = 1979, Format = ReleaseFormat.Dvd });
        provider.Results.Add(new ProviderRelease { Provider = "tmdb", ExternalId = "2", Title = "Aliens", Kind = WorkKind.Film, Year = 1986 });
        var client = ClientWith(await SomeUserAsync(), provider);

        var items = await ReadItemsAsync(client, "q=alien");

        items.Select(i => i.GetProperty("work").GetProperty("title").GetString()).ShouldBe(["Alien", "Aliens"]);
        items.Select(i => i.GetProperty("source").GetString()).ShouldBe(["local", "tmdb"]);
    }

    [Fact]
    public async Task Provider_editions_of_one_book_arrive_as_one_work_with_every_edition()
    {
        // The Suddenly You shape (#438): one title and author, two years, different publishers.
        var provider = new FakeProvider("googlebooks");
        provider.Results.Add(new ProviderRelease { Provider = "googlebooks", ExternalId = "v1", Title = "Suddenly You", Kind = WorkKind.Book, Year = 2001, Creators = ["Lisa Kleypas"], Publisher = "Avon", Format = ReleaseFormat.Paperback });
        provider.Results.Add(new ProviderRelease { Provider = "googlebooks", ExternalId = "v2", Title = "Suddenly You", Kind = WorkKind.Book, Year = 2003, Creators = ["Lisa Kleypas"], Publisher = "St. Martin's Press", Format = ReleaseFormat.MassMarket });
        var client = ClientWith(await SomeUserAsync(), provider);

        var items = await ReadItemsAsync(client, "q=suddenly you");

        items.Count.ShouldBe(1);
        items[0].GetProperty("work").GetProperty("title").GetString().ShouldBe("Suddenly You");
        items[0].GetProperty("releases").EnumerateArray().Select(r => r.GetProperty("year").GetInt32()).Order().ShouldBe([2001, 2003]);
    }

    [Fact]
    public async Task The_same_book_from_two_providers_groups_into_one_work()
    {
        var openLibrary = new FakeProvider("openlibrary");
        openLibrary.Results.Add(new ProviderRelease { Provider = "openlibrary", ExternalId = "OL123W", WorkKey = "OL123W", Title = "Suddenly You", Kind = WorkKind.Book, Year = 2001, Creators = ["Lisa Kleypas"], Publisher = "Avon" });
        var google = new FakeProvider("googlebooks");
        google.Results.Add(new ProviderRelease { Provider = "googlebooks", ExternalId = "v9", Title = "Suddenly You", Kind = WorkKind.Book, Year = 2003, Creators = ["Lisa Kleypas"], Publisher = "St. Martin's Press", Format = ReleaseFormat.Paperback });
        var client = ClientWith(await SomeUserAsync(), openLibrary, google);

        var items = await ReadItemsAsync(client, "q=suddenly you");

        items.Count.ShouldBe(1);
        items[0].GetProperty("releases").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Provider_hits_sharing_a_provider_work_key_group_into_one_work()
    {
        var provider = new FakeProvider("igdb");
        provider.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "1074-n64", WorkKey = "1074", Title = "Super Mario 64", Kind = WorkKind.Game, Platform = "Nintendo 64", Year = 1996, Creators = ["Nintendo EAD"] });
        provider.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "1074-switch", WorkKey = "1074", Title = "Super Mario 64", Kind = WorkKind.Game, Platform = "Nintendo Switch", Year = 1996, Creators = ["Nintendo EAD"] });
        var client = ClientWith(await SomeUserAsync(), provider);

        var items = await ReadItemsAsync(client, "q=super mario 64");

        items.Count.ShouldBe(1);
        items[0].GetProperty("releases").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Provider_hits_with_different_work_keys_from_one_provider_stay_apart()
    {
        var provider = new FakeProvider("igdb");
        provider.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "1", WorkKey = "1", Title = "Doom", Kind = WorkKind.Game, Year = 1993, Creators = ["id Software"] });
        provider.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "2", WorkKey = "2", Title = "Doom", Kind = WorkKind.Game, Year = 2016, Creators = ["id Software"] });
        var client = ClientWith(await SomeUserAsync(), provider);

        var items = await ReadItemsAsync(client, "q=doom");

        items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Same_title_films_from_different_years_stay_apart()
    {
        var provider = new FakeProvider("tmdb");
        provider.Results.Add(new ProviderRelease { Provider = "tmdb", ExternalId = "a", Title = "Total Recall", Kind = WorkKind.Film, Year = 1990 });
        provider.Results.Add(new ProviderRelease { Provider = "tmdb", ExternalId = "b", Title = "Total Recall", Kind = WorkKind.Film, Year = 2012 });
        var client = ClientWith(await SomeUserAsync(), provider);

        var items = await ReadItemsAsync(client, "q=total recall");

        items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Local_editions_of_one_work_arrive_as_one_result_with_every_edition()
    {
        var (workId, _) = await CatalogSeed.AddAsync(Db, "Alien", WorkKind.Film, 1979);
        await using (var context = Db.CreateContext())
        {
            context.Releases.Add(new Release
            {
                Id = Guid.CreateVersion7(),
                WorkId = workId,
                Format = ReleaseFormat.Vhs,
                Source = ReleaseSource.Provider,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
            await context.SaveChangesAsync();
        }

        var items = await ReadItemsAsync(ClientWith(await SomeUserAsync()), "q=alien");

        items.Count.ShouldBe(1);
        items[0].GetProperty("releases").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Search_results_carry_the_releases_you_own()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Jurassic Park");
        var (_, ownedReleaseId) = await CatalogSeed.AddAsync(Db, "Jurassic Park III", year: 2001);
        await TestData.CreateItemAsync(Db, library, ownedReleaseId);
        var client = ClientWith(library.ViewerId);

        var items = await ReadItemsAsync(client, "q=jurassic park");

        items.Count.ShouldBe(2);
        var owned = items.Single(i => i.GetProperty("work").GetProperty("title").GetString() == "Jurassic Park III");
        owned.GetProperty("ownedReleaseIds").EnumerateArray().Select(g => g.GetGuid()).ShouldBe([ownedReleaseId]);
        items.Where(i => i.GetProperty("work").GetProperty("title").GetString() != "Jurassic Park III")
            .SelectMany(i => i.GetProperty("ownedReleaseIds").EnumerateArray()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Owned_ids_never_carry_what_another_library_owns()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var (_, releaseId) = await CatalogSeed.AddAsync(Db, "Jurassic Park");
        await TestData.CreateItemAsync(Db, theirs, releaseId);

        var mineItems = await ReadItemsAsync(ClientWith(mine.ViewerId), "q=jurassic park");
        var theirsItems = await ReadItemsAsync(ClientWith(theirs.ViewerId), "q=jurassic park");

        mineItems.Single().GetProperty("ownedReleaseIds").GetArrayLength().ShouldBe(0);
        theirsItems.Single().GetProperty("ownedReleaseIds").EnumerateArray().Select(g => g.GetGuid()).ShouldBe([releaseId]);
    }

    [Fact]
    public async Task Another_librarys_manual_title_is_never_returned_by_search()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Grandpa's Home Movies", ownerLibraryId: theirs.Id);
        await CatalogSeed.AddAsync(Db, "Grandpa's Home Movies", ownerLibraryId: mine.Id, year: 1985);

        var outsider = await TitlesAsync(await ClientWith(theirs.OutsiderId).GetAsync("/api/v1/catalog/search?q=grandpa home movies"));
        var member = await ReadItemsAsync(ClientWith(mine.ViewerId), "q=grandpa home movies");
        var other = await ReadItemsAsync(ClientWith(theirs.ViewerId), "q=grandpa home movies");

        outsider.ShouldBeEmpty();
        member.Count.ShouldBe(1);
        member[0].GetProperty("work").GetProperty("originalYear").GetInt32().ShouldBe(1985);
        other.Count.ShouldBe(1);
        other[0].GetProperty("work").GetProperty("originalYear").GetInt32().ShouldBe(1999);
    }

    [Fact]
    public async Task A_global_work_shows_only_the_releases_the_caller_may_see()
    {
        var library = await CreateLibraryAsync();
        var (workId, _) = await CatalogSeed.AddAsync(Db, "Alien");
        await using (var context = Db.CreateContext())
        {
            context.Releases.Add(new Release
            {
                Id = Guid.CreateVersion7(),
                WorkId = workId,
                Format = ReleaseFormat.Vhs,
                Source = ReleaseSource.User,
                OwnerLibraryId = library.Id,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
            await context.SaveChangesAsync();
        }

        var asOutsider = await ReadItemsAsync(ClientWith(library.OutsiderId), "q=alien");
        var asMember = await ReadItemsAsync(ClientWith(library.ViewerId), "q=alien");

        asOutsider[0].GetProperty("releases").GetArrayLength().ShouldBe(1);
        asMember[0].GetProperty("releases").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Lookup_of_a_upc_a_and_its_gtin_13_form_returns_the_same_release()
    {
        var (_, releaseId) = await CatalogSeed.AddAsync(Db, "Jurassic Park", barcode: "0045496870010");
        var client = ClientWith(await SomeUserAsync());

        var upc = await ReadJsonAsync(await client.GetAsync("/api/v1/catalog/lookup?code=045496870010"));
        var gtin = await ReadJsonAsync(await client.GetAsync("/api/v1/catalog/lookup?code=0045496870010"));

        upc.GetProperty("releases").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe([releaseId]);
        gtin.GetProperty("releases").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe([releaseId]);
    }

    [Fact]
    public async Task Lookup_with_a_bad_check_digit_is_a_400_problem()
    {
        var response = await ClientWith(await SomeUserAsync()).GetAsync("/api/v1/catalog/lookup?code=0045496870011");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await ReadJsonAsync(response)).GetProperty("errors").TryGetProperty("code", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Lookup_of_an_unknown_code_is_an_empty_result_not_an_error()
    {
        var response = await ClientWith(await SomeUserAsync()).GetAsync("/api/v1/catalog/lookup?code=0045496870010");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("releases").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Lookup_asks_the_providers_only_when_the_catalogue_has_nothing_and_survives_a_failing_one()
    {
        var broken = new FakeProvider("broken") { Behaviour = _ => throw new InvalidOperationException("boom") };
        var good = new FakeProvider("upcitemdb")
        {
            LookupResult = new ProviderRelease { Provider = "upcitemdb", ExternalId = "x", Title = "Jurassic Park", Kind = WorkKind.Film, Year = 1993 },
        };
        var client = ClientWith(await SomeUserAsync(), broken, good);

        var body = await ReadJsonAsync(await client.GetAsync("/api/v1/catalog/lookup?code=0045496870010"));

        body.GetProperty("releases").EnumerateArray().Select(r => r.GetProperty("title").GetString()).ShouldBe(["Jurassic Park"]);
    }

    [Fact]
    public async Task A_private_release_is_invisible_to_other_libraries_in_lookup_release_and_artwork_and_visible_to_members()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var cover = await CatalogSeed.AddAssetAsync(Db, "a/b.png", ownerLibraryId: mine.Id);
        var (_, releaseId) = await CatalogSeed.AddAsync(Db, "Home Tape", ownerLibraryId: mine.Id, barcode: "0045496870010", coverAssetId: cover);

        foreach (var role in new[] { LibraryRole.Viewer, LibraryRole.Editor, LibraryRole.Owner })
        {
            var client = ClientWith(mine.UserWithRole(role));
            (await client.GetAsync($"/api/v1/catalog/releases/{releaseId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
            var artwork = await ReadJsonAsync(await client.GetAsync($"/api/v1/catalog/releases/{releaseId}/artwork"));
            artwork.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("assetId").GetGuid()).ShouldBe([cover]);
            (await ReadJsonAsync(await client.GetAsync("/api/v1/catalog/lookup?code=0045496870010")))
                .GetProperty("releases").GetArrayLength().ShouldBe(1);
        }

        foreach (var userId in new[] { theirs.OwnerId, theirs.OutsiderId })
        {
            var client = ClientWith(userId);
            (await client.GetAsync($"/api/v1/catalog/releases/{releaseId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.GetAsync($"/api/v1/catalog/releases/{releaseId}/artwork")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ReadJsonAsync(await client.GetAsync("/api/v1/catalog/lookup?code=0045496870010")))
                .GetProperty("releases").GetArrayLength().ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_global_release_shows_its_global_artwork_but_not_a_private_asset_the_caller_cannot_see()
    {
        var library = await CreateLibraryAsync();
        var global = await CatalogSeed.AddAssetAsync(Db, "g/cover.png");
        var (_, releaseId) = await CatalogSeed.AddAsync(Db, "Alien", coverAssetId: global);

        var body = await ReadJsonAsync(await ClientWith(library.OutsiderId).GetAsync($"/api/v1/catalog/releases/{releaseId}/artwork"));

        var art = body.GetProperty("items").EnumerateArray().ToList();
        art.Count.ShouldBe(1);
        art[0].GetProperty("url").GetString().ShouldBe($"/media/covers/{global}");
    }

    [Fact]
    public async Task An_unknown_release_is_404()
    {
        var client = ClientWith(await SomeUserAsync());

        (await client.GetAsync($"/api/v1/catalog/releases/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/v1/catalog/releases/{Guid.NewGuid()}/artwork")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Platforms_are_listed_by_name()
    {
        await CatalogSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        await CatalogSeed.AddPlatformAsync(Db, "Atari 2600", "2600");

        var body = await ReadJsonAsync(await ClientWith(await SomeUserAsync()).GetAsync("/api/v1/catalog/platforms"));

        body.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(["Atari 2600", "Nintendo 64"]);
    }

    [Fact]
    public async Task Anonymous_callers_get_401()
    {
        var client = ClientWith(null);

        (await client.GetAsync("/api/v1/catalog/search?q=alien")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/catalog/platforms")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<List<JsonElement>> ReadItemsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/api/v1/catalog/search?" + query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
    }
}
