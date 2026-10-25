using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Infrastructure.Providers.Igdb;
using CollectionMaxxing.Infrastructure.Providers.Tmdb;
using CollectionMaxxing.TestSupport;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

public class TmdbProviderTests(PostgresFixture db) : DatabaseTest(db)
{
    private static void Stub(ProviderHost host, string path, string fixture) =>
        host.Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(ProviderHost.Fixture(fixture)));

    [Fact]
    public async Task Searches_films_by_title_and_year()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "/3/search/movie", "tmdb-movie-search.json");

        var results = await host.Get<TmdbProvider>().SearchAsync(
            new CatalogQuery { Title = "Casablanca", Year = 1943, Kind = WorkKind.Film }, default);

        results.Count.ShouldBe(2);
        results[0].ExternalId.ShouldBe("movie/289");
        results[0].Title.ShouldBe("Casablanca");
        results[0].Kind.ShouldBe(WorkKind.Film);
        results[0].Year.ShouldBe(1943);
        results[0].CoverUrl.ShouldBe("https://image.tmdb.org/t/p/w500/5K7cOHoay2mZusSLezBOY0Qxh8a.jpg");
        results[1].CoverUrl.ShouldBeNull();

        var query = host.Server.LogEntries.Single().RequestMessage!.Query!;
        query["query"].ToString().ShouldBe("Casablanca");
        query["year"].ToString().ShouldBe("1943");
    }

    [Fact]
    public async Task Searches_films_and_television_when_the_kind_is_unknown()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "/3/search/movie", "tmdb-movie-search.json");
        Stub(host, "/3/search/tv", "tmdb-tv-search.json");

        var results = await host.Get<TmdbProvider>().SearchAsync(new CatalogQuery { Title = "Fantasy", Limit = 10 }, default);

        results.Select(r => r.Kind).ShouldBe([WorkKind.Film, WorkKind.Tv, WorkKind.Film]);
        results[1].ExternalId.ShouldBe("tv/1396");
        results[1].Title.ShouldBe("Fantasy Show");
        results[1].Year.ShouldBe(2008);
    }

    [Fact]
    public async Task Mixed_search_keeps_television_when_films_alone_would_fill_the_limit()
    {
        // Issue #292: films and television merge before the limit cuts, so a full film page starves nothing.
        using var host = ProviderHost.Create(Db);
        Stub(host, "/3/search/movie", "tmdb-movie-search.json");
        Stub(host, "/3/search/tv", "tmdb-tv-search.json");

        var results = await host.Get<TmdbProvider>().SearchAsync(new CatalogQuery { Title = "Fantasy", Limit = 2 }, default);

        results.Select(r => r.Kind).ShouldBe([WorkKind.Film, WorkKind.Tv]);
    }

    [Fact]
    public async Task Sends_a_v4_read_token_as_a_bearer_header()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:Tmdb:ApiKey"] = "eyJ-test-read-token" });
        Stub(host, "/3/search/movie", "tmdb-movie-search.json");

        await host.Get<TmdbProvider>().SearchAsync(new CatalogQuery { Title = "Casablanca", Kind = WorkKind.Film }, default);

        var request = host.Server.LogEntries.Single().RequestMessage!;
        request.Headers!["Authorization"].ToString().ShouldBe("Bearer eyJ-test-read-token");
        request.Query!.Keys.ShouldNotContain("api_key");
    }

    [Fact]
    public async Task Sends_a_v3_key_as_the_api_key_value()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, "/3/search/movie", "tmdb-movie-search.json");

        await host.Get<TmdbProvider>().SearchAsync(new CatalogQuery { Title = "Casablanca", Kind = WorkKind.Film }, default);

        host.Server.LogEntries.Single().RequestMessage!.Query!["api_key"].ToString().ShouldBe("tmdb-test-key");
    }

    [Fact]
    public async Task Is_switched_off_without_a_key_and_makes_no_calls()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:Tmdb:ApiKey"] = null });
        var provider = host.Get<TmdbProvider>();

        provider.Supports(WorkKind.Film).ShouldBeFalse();
        (await provider.SearchAsync(new CatalogQuery { Title = "Casablanca" }, default)).ShouldBeEmpty();
        host.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Has_no_barcode_lookup()
    {
        using var host = ProviderHost.Create(Db);

        (await host.Get<TmdbProvider>().LookupAsync(ProviderCase.Upc, default)).ShouldBeNull();
        host.Server.LogEntries.ShouldBeEmpty();
    }
}

public class IgdbProviderTests(PostgresFixture db) : DatabaseTest(db)
{
    private static CatalogQuery Mario => new() { Title = "Super Mario 64", Kind = WorkKind.Game };

    [Fact]
    public async Task Searches_games_and_maps_platform_year_companies_and_box_art()
    {
        using var host = ProviderHost.Create(Db);
        ProviderCase.Named("igdb").RespondWith(host, 200, "igdb-games.json");

        var results = await host.Get<IgdbProvider>().SearchAsync(Mario, default);

        results.Count.ShouldBe(2);
        var game = results[0];
        game.ExternalId.ShouldBe("1074");
        game.Title.ShouldBe("Super Mario 64");
        game.Kind.ShouldBe(WorkKind.Game);
        game.Platform.ShouldBe("Nintendo 64");
        game.Year.ShouldBe(1996);
        game.Creators.ShouldBe(["Nintendo EAD"]);
        game.Publisher.ShouldBe("Nintendo");
        game.CoverUrl.ShouldBe("https://images.igdb.com/igdb/image/upload/t_cover_big/co1xaz.jpg");

        var request = host.Server.LogEntries.Single(e => e.RequestMessage!.Path == "/v4/games").RequestMessage!;
        request.Headers!["Client-ID"].ToString().ShouldBe("twitch-test-id");
        request.Headers["Authorization"].ToString().ShouldBe("Bearer test-token-1");
        request.Body!.ShouldContain("search \"Super Mario 64\"");
    }

    [Fact]
    public async Task Prefers_the_platform_that_was_asked_for_and_drops_games_not_on_it()
    {
        using var host = ProviderHost.Create(Db);
        ProviderCase.Named("igdb").RespondWith(host, 200, "igdb-games.json");

        var switchOnly = await host.Get<IgdbProvider>().SearchAsync(Mario with { Platform = "Switch" }, default);
        var wii = await host.Get<IgdbProvider>().SearchAsync(Mario with { Platform = "Wii" }, default);

        switchOnly.Select(r => (r.Title, r.Platform)).ShouldBe([("Super Mario 64", "Nintendo Switch")]);
        wii.ShouldBeEmpty();
    }

    [Fact]
    public async Task Fetches_the_token_once_and_reuses_it()
    {
        using var host = ProviderHost.Create(Db);
        ProviderCase.Named("igdb").RespondWith(host, 200, "igdb-games.json");

        await host.Get<IgdbProvider>().SearchAsync(Mario, default);
        await host.Get<IgdbProvider>().SearchAsync(Mario with { Title = "Mario Kart 64" }, default);

        host.Requests("/oauth2/token").ShouldBe(1);
        host.Requests("/v4/games").ShouldBe(2);
    }

    [Fact]
    public async Task Refreshes_the_token_once_on_a_401_and_retries_with_the_new_one()
    {
        using var host = ProviderHost.Create(Db);
        var tokens = 0;
        host.Server.Given(Request.Create().WithPath("/oauth2/token").UsingPost()).RespondWith(Response.Create().WithCallback(_ =>
        {
            var message = new WireMock.ResponseMessage
            {
                StatusCode = 200,
                BodyData = new WireMock.Util.BodyData
                {
                    BodyAsString = ProviderHost.Fixture(Interlocked.Increment(ref tokens) == 1 ? "igdb-token.json" : "igdb-token-2.json"),
                    DetectedBodyType = WireMock.Types.BodyType.String,
                },
            };
            message.AddHeader("Content-Type", "application/json");
            return message;
        }));
        host.Server.Given(Request.Create().WithPath("/v4/games").UsingPost().WithHeader("Authorization", "Bearer test-token-1"))
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("{}"));
        host.Server.Given(Request.Create().WithPath("/v4/games").UsingPost().WithHeader("Authorization", "Bearer test-token-2"))
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(ProviderHost.Fixture("igdb-games.json")));

        var results = await host.Get<IgdbProvider>().SearchAsync(Mario, default);

        results.Count.ShouldBe(2);
        host.Requests("/oauth2/token").ShouldBe(2);
        host.Requests("/v4/games").ShouldBe(2);
    }

    [Fact]
    public async Task Does_not_loop_when_the_new_token_is_refused_too()
    {
        using var host = ProviderHost.Create(Db);
        host.StubIgdbToken();
        host.Server.Given(Request.Create().WithPath("/v4/games").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("{}"));

        (await host.Get<IgdbProvider>().SearchAsync(Mario, default)).ShouldBeEmpty();

        host.Requests("/oauth2/token").ShouldBe(2); // the first token and one refresh
        host.Requests("/v4/games").ShouldBe(2); // the first try and one retry, then it stops
    }

    [Fact]
    public async Task Gives_up_quietly_when_no_token_can_be_had()
    {
        using var host = ProviderHost.Create(Db);
        host.Server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("{}"));
        host.Server.Given(Request.Create().WithPath("/v4/games").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("{}"));

        (await host.Get<IgdbProvider>().SearchAsync(Mario, default)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Providers:Igdb:ClientId")]
    [InlineData("Providers:Igdb:ClientSecret")]
    public async Task Is_switched_off_without_the_twitch_client_id_or_secret(string missing)
    {
        using var host = ProviderHost.Create(Db, settings: new() { [missing] = null });
        var provider = host.Get<IgdbProvider>();

        provider.Supports(WorkKind.Game).ShouldBeFalse();
        (await provider.SearchAsync(Mario, default)).ShouldBeEmpty();
        host.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Has_no_barcode_lookup()
    {
        using var host = ProviderHost.Create(Db);

        (await host.Get<IgdbProvider>().LookupAsync(ProviderCase.Upc, default)).ShouldBeNull();
        host.Server.LogEntries.ShouldBeEmpty();
    }
}

public class ProviderRegistrationTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public void Registers_all_five_providers_and_a_missing_key_switches_off_only_that_provider()
    {
        using var host = ProviderHost.Create(Db, settings: new()
        {
            ["Providers:GoogleBooks:ApiKey"] = null,
            ["Providers:Tmdb:ApiKey"] = null,
            ["Providers:Igdb:ClientSecret"] = null,
        });

        var providers = host.Get<IEnumerable<ICatalogProvider>>().ToDictionary(p => p.Name);

        providers.Keys.Order().ShouldBe(["googlebooks", "igdb", "openlibrary", "tmdb", "upcitemdb"]);
        providers["openlibrary"].Supports(WorkKind.Book).ShouldBeTrue();
        providers["upcitemdb"].Supports(WorkKind.Game).ShouldBeTrue();
        providers["googlebooks"].Supports(WorkKind.Book).ShouldBeFalse();
        providers["tmdb"].Supports(WorkKind.Film).ShouldBeFalse();
        providers["igdb"].Supports(WorkKind.Game).ShouldBeFalse();
    }

    [Fact]
    public void With_every_key_set_each_provider_supports_its_kinds()
    {
        using var host = ProviderHost.Create(Db);

        var providers = host.Get<IEnumerable<ICatalogProvider>>().ToDictionary(p => p.Name);

        providers["googlebooks"].Supports(WorkKind.Book).ShouldBeTrue();
        providers["tmdb"].Supports(WorkKind.Film).ShouldBeTrue();
        providers["tmdb"].Supports(WorkKind.Tv).ShouldBeTrue();
        providers["tmdb"].Supports(WorkKind.Game).ShouldBeFalse();
        providers["igdb"].Supports(WorkKind.Game).ShouldBeTrue();
        providers["igdb"].Supports(WorkKind.Book).ShouldBeFalse();
    }
}
