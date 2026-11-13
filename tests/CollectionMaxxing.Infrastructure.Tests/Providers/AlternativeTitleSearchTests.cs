using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Infrastructure.Providers.Tmdb;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>
/// #828, end to end through the TMDB adapter. The reader read "HIGHLANDER THE FINAL DIMENSION" off a tape; the catalogue lists that film as
/// "Highlander III: The Sorcerer" and holds the title on the tape among its alternative titles. WireMock answers in the shape TMDB documents for
/// <c>/3/search/movie</c>, <c>/3/movie/{id}/alternative_titles</c> (titles under "titles") and <c>/3/tv/{id}/alternative_titles</c>
/// (titles under "results"); no real TMDB reply was seen.
/// </summary>
public class AlternativeTitleSearchTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string Read = "HIGHLANDER THE FINAL DIMENSION";

    private const string Highlanders = """
        {"page":1,"results":[
          {"adult":false,"id":8010,"original_title":"Highlander: Endgame","overview":"","poster_path":null,"release_date":"2000-09-01","title":"Highlander: Endgame"},
          {"adult":false,"id":8011,"original_title":"Highlander II: The Quickening","overview":"","poster_path":null,"release_date":"1991-03-15","title":"Highlander II: The Quickening"},
          {"adult":false,"id":8012,"original_title":"Highlander III: The Sorcerer","overview":"","poster_path":null,"release_date":"1994-10-26","title":"Highlander III: The Sorcerer"}
        ],"total_pages":1,"total_results":3}
        """;

    private const string SorcererTitles = """
        {"id":8012,"titles":[
          {"iso_3166_1":"DE","title":"Highlander 3 - Die Magie","type":""},
          {"iso_3166_1":"US","title":"Highlander: The Final Dimension","type":"working title"}
        ]}
        """;

    private const string OtherTitles = """{"id":1,"titles":[{"iso_3166_1":"FR","title":"Highlander, le retour","type":""}]}""";

    private const string Nothing = """{"page":1,"results":[],"total_pages":0,"total_results":0}""";

    private sealed class EmptyCatalogue : ILocalCatalogSearch
    {
        public Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalHit>>([]);
    }

    private sealed class FixedScoring : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue { get; } = new();

        public ScoringOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ScoringOptions, string?> listener) => null;
    }

    private static void Answer(ProviderHost host, string path, string body, int status = 200)
    {
        host.Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));
    }

    private static IdentifyGrounding Grounding(ProviderHost host) => new(
        new TextSearchResolver(null!, new EmptyCatalogue(), [host.Get<TmdbProvider>()], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    private static Task<BarcodeResolution> GroundAsync(ProviderHost host, string read = Read, ScoringProfileKind profile = ScoringProfileKind.ShelfSpine) =>
        Grounding(host).GroundAsync(
            ReadSanitizer.Sanitize(new ReadFacts(read, null, null, null, null, WorkKind.Film, null, null, null)), profile, TimeSpan.FromSeconds(30), CancellationToken.None);

    private static List<string> AlternativeTitleCalls(ProviderHost host) =>
        [.. host.Server.LogEntries.Select(e => e.RequestMessage!.Path).Where(p => p!.EndsWith("/alternative_titles", StringComparison.Ordinal))];

    private ProviderHost Host()
    {
        var host = ProviderHost.Create(Db);
        Answer(host, "/3/search/movie", Highlanders);
        Answer(host, "/3/search/tv", Nothing);
        return host;
    }

    [Fact]
    public async Task A_film_read_by_its_alternative_title_is_matched_to_the_film_that_has_it()
    {
        using var host = Host();
        Answer(host, "/3/movie/8010/alternative_titles", OtherTitles);
        Answer(host, "/3/movie/8011/alternative_titles", OtherTitles);
        Answer(host, "/3/movie/8012/alternative_titles", SorcererTitles);

        var answer = await GroundAsync(host);

        answer.Verdict.ShouldBe(ResolutionVerdict.Matched);
        answer.Options[0].Provider!.ExternalId.ShouldBe("movie/8012");
    }

    [Fact]
    public async Task The_shown_title_stays_the_main_title()
    {
        using var host = Host();
        Answer(host, "/3/movie/8010/alternative_titles", OtherTitles);
        Answer(host, "/3/movie/8011/alternative_titles", OtherTitles);
        Answer(host, "/3/movie/8012/alternative_titles", SorcererTitles);

        var answer = await GroundAsync(host);

        answer.Options[0].Provider!.Title.ShouldBe("Highlander III: The Sorcerer");
        answer.Options[0].Provider!.Year.ShouldBe(1994);
    }

    [Fact]
    public async Task A_read_that_is_already_matched_asks_for_no_alternative_titles()
    {
        using var host = Host();
        Answer(host, "/3/search/movie", """
            {"page":1,"results":[{"id":8009,"original_title":"Highlander","overview":"","poster_path":null,"release_date":"1986-03-07","title":"Highlander"}]}
            """);

        var answer = await GroundAsync(host, "HIGHLANDER");

        answer.Verdict.ShouldBe(ResolutionVerdict.Matched);
        AlternativeTitleCalls(host).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_candidate_whose_alternative_titles_were_asked_before_is_answered_from_the_cache()
    {
        using var host = Host();
        Answer(host, "/3/movie/8010/alternative_titles", OtherTitles);
        Answer(host, "/3/movie/8011/alternative_titles", OtherTitles);
        Answer(host, "/3/movie/8012/alternative_titles", SorcererTitles);

        await GroundAsync(host);
        var callsAfterFirst = AlternativeTitleCalls(host).Count;
        var again = await GroundAsync(host);

        callsAfterFirst.ShouldBe(3);
        AlternativeTitleCalls(host).Count.ShouldBe(3);
        again.Options[0].Provider!.ExternalId.ShouldBe("movie/8012");
    }

    [Fact]
    public async Task A_failed_alternative_titles_call_leaves_the_candidates_as_they_were()
    {
        using var host = Host();
        Answer(host, "/3/movie/8010/alternative_titles", "{}", status: 500);
        Answer(host, "/3/movie/8011/alternative_titles", "{}", status: 500);
        Answer(host, "/3/movie/8012/alternative_titles", "{}", status: 500);
        using var report = ProviderCalls.Observe();

        var answer = await GroundAsync(host);

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options[0].Provider!.Title.ShouldBe("Highlander: Endgame");
        answer.Options.Select(o => o.Provider!.Title).ShouldBe(["Highlander: Endgame", "Highlander II: The Quickening", "Highlander III: The Sorcerer"]);
        report.Failed.ShouldBeFalse("the optional call's failure must not make the whole search look unanswered");
    }

    [Fact]
    public async Task A_failed_call_is_not_remembered_so_the_next_read_asks_again()
    {
        using var host = Host();
        Answer(host, "/3/movie/8010/alternative_titles", "{}", status: 500);
        Answer(host, "/3/movie/8011/alternative_titles", "{}", status: 500);
        Answer(host, "/3/movie/8012/alternative_titles", "{}", status: 500);
        await GroundAsync(host);

        host.Server.Reset();
        Answer(host, "/3/search/movie", Highlanders);
        Answer(host, "/3/search/tv", Nothing);
        Answer(host, "/3/movie/8010/alternative_titles", OtherTitles);
        Answer(host, "/3/movie/8011/alternative_titles", OtherTitles);
        Answer(host, "/3/movie/8012/alternative_titles", SorcererTitles);
        var answer = await GroundAsync(host);

        answer.Options[0].Provider!.ExternalId.ShouldBe("movie/8012");
    }

    [Fact]
    public async Task A_tv_show_is_asked_on_the_tv_endpoint_and_its_titles_are_read_from_results()
    {
        using var host = ProviderHost.Create(Db);
        Answer(host, "/3/search/movie", Nothing);
        Answer(host, "/3/search/tv", """
            {"page":1,"results":[
              {"id":501,"original_name":"Kaiju Hour","overview":"","poster_path":null,"first_air_date":"2001-04-01","name":"Kaiju Hour"},
              {"id":502,"original_name":"Kaiju Hour Reloaded","overview":"","poster_path":null,"first_air_date":"2009-04-01","name":"Kaiju Hour Reloaded"}
            ]}
            """);
        Answer(host, "/3/tv/501/alternative_titles", """{"id":501,"results":[{"iso_3166_1":"JP","title":"Monster Parade Weekly","type":""}]}""");
        Answer(host, "/3/tv/502/alternative_titles", """{"id":502,"results":[]}""");

        var answer = await Grounding(host).GroundAsync(
            new ReadFacts("KAIJU HOUR MONSTER PARADE WEEKLY", null, null, null, null, WorkKind.Tv, null, null, null), ScoringProfileKind.ShelfSpine, TimeSpan.FromSeconds(30), CancellationToken.None);

        AlternativeTitleCalls(host).Order().ShouldBe(["/3/tv/501/alternative_titles", "/3/tv/502/alternative_titles"]);
        answer.Options[0].Provider!.Title.ShouldBe("Kaiju Hour");
    }
}
