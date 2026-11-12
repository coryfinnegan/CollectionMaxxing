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
/// #815, end to end through the TMDB adapter (WireMock answers in the shape of TMDB's <c>/3/search/movie</c>): the server's reader read
/// "HIGHLANDER: DIRECTOR'S CUT" off a case, and the match came back as a film called "Cut!" with a perfect score. The possessive retry
/// had read "HIGHLANDER: DIRECTOR'S" as a name and searched what was left, "CUT". Now the edition phrase is split off before anything is
/// asked, so TMDB is asked for "HIGHLANDER" and never for "CUT".
/// </summary>
public class EditionPhraseSearchTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string Highlanders = """
        {"page":1,"results":[
          {"adult":false,"id":8009,"original_language":"en","original_title":"Highlander","overview":"","poster_path":null,"release_date":"1986-03-07","title":"Highlander","video":false},
          {"adult":false,"id":8010,"original_language":"en","original_title":"Highlander II: The Quickening","overview":"","poster_path":null,"release_date":"1991-03-15","title":"Highlander II: The Quickening","video":false}
        ],"total_pages":1,"total_results":2}
        """;

    private const string Cuts = """
        {"page":1,"results":[
          {"adult":false,"id":900001,"original_language":"en","original_title":"Cut!","overview":"","poster_path":null,"release_date":"2021-05-01","title":"Cut!","video":false}
        ],"total_pages":1,"total_results":1}
        """;

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

    private static void Answer(ProviderHost host, string path, string body, string? query = null, int priority = 10)
    {
        var request = Request.Create().WithPath(path).UsingGet();
        if (query is not null)
        {
            request = request.WithParam("query", true, query);
        }

        host.Server.Given(request).AtPriority(priority)
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(body));
    }

    private static List<string> MovieQueries(ProviderHost host) =>
        [.. host.Server.LogEntries.Where(e => e.RequestMessage!.Path == "/3/search/movie").Select(e => e.RequestMessage!.Query!["query"].ToString())];

    [Theory]
    [InlineData("HIGHLANDER: DIRECTOR'S CUT")]
    [InlineData("HIGHLANDER DIRECTOR'S CUT")]
    public async Task A_film_read_with_an_edition_phrase_after_its_title_is_matched_as_the_title_alone(string read)
    {
        using var host = ProviderHost.Create(Db);
        Answer(host, "/3/search/movie", Highlanders, "HIGHLANDER", priority: 1);
        Answer(host, "/3/search/movie", Cuts, "CUT", priority: 1);
        Answer(host, "/3/search/movie", Nothing);
        Answer(host, "/3/search/tv", Nothing);
        var grounding = new IdentifyGrounding(
            new TextSearchResolver(null!, new EmptyCatalogue(), [host.Get<TmdbProvider>()], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
            new FixedScoring());

        // As the server's reader sends it: a title and a kind, no text (ServerReadInput), cleaned as every read is.
        var facts = ReadSanitizer.Sanitize(new ReadFacts(read, null, null, null, null, WorkKind.Film, null, null, null));
        var answer = await grounding.GroundAsync(facts, ScoringProfileKind.ShelfSpine, TimeSpan.FromSeconds(30), CancellationToken.None);

        answer.Options[0].Provider!.Title.ShouldBe("Highlander");
        answer.Options[0].Provider!.Year.ShouldBe(1986);
        answer.Verdict.ShouldBe(ResolutionVerdict.Matched);
        answer.Options.ShouldNotContain(o => o.Provider!.Title == "Cut!");
        MovieQueries(host).ShouldBe(["HIGHLANDER"]);
    }
}
