using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// On a film shelf the picture model can call a film a game. A game read that found nothing sure is asked once more as any kind
// (no kind, platform or format), and what that finds is offered, never matched; the read's own best release stays on top unless
// the find scores higher. Only a spine on a shelf is asked this, and only a read that names a game.
public class IdentifyGroundingAnyKindRetryTests
{
    private const string Title = "Harbour Lights";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static ReadFacts SpineRead(WorkKind kind, string title = Title) => new(
        title, title, null, null, null, kind, kind == WorkKind.Game ? null : ReleaseFormat.Vhs, null, kind == WorkKind.Game ? "PlayStation" : null);

    /// <summary>A provider that knows films and games, asked only for a kind it supports (or any kind), and finds the ones titled as asked.</summary>
    private sealed class MediaProvider(WorkKind kind, params string[] titles) : ICatalogProvider
    {
        public List<(string Title, WorkKind? Kind)> Asked { get; } = [];

        public string Name => kind == WorkKind.Film ? "tmdb" : "igdb";

        public bool Supports(WorkKind asked) => asked == kind;

        public Task<ProviderRelease?> LookupAsync(CollectionMaxxing.Domain.Identifiers.Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            Asked.Add((query.Title, query.Kind));
            return Task.FromResult<IReadOnlyList<ProviderRelease>>([.. titles
                .Where(t => string.Equals(t, query.Title, StringComparison.OrdinalIgnoreCase))
                .Select(t => new ProviderRelease
                {
                    Provider = Name,
                    ExternalId = "x" + t.Length,
                    Title = t,
                    Kind = kind,
                    Format = kind == WorkKind.Film ? ReleaseFormat.Dvd : ReleaseFormat.GameDisc,
                    Year = 1999,
                })]);
        }
    }

    private static Task<BarcodeResolution> GroundAsync(ReadFacts facts, ScoringProfileKind profile, params MediaProvider[] providers)
    {
        var grounding = new IdentifyGrounding(
            new TextSearchResolver(null!, new Catalogue(), providers, new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
            new FixedScoring());
        return grounding.GroundAsync(facts, profile, Budget, CancellationToken.None);
    }

    [Fact]
    public async Task A_spine_read_as_a_game_that_finds_only_games_on_other_platforms_is_asked_as_any_kind_and_the_film_it_finds_is_offered()
    {
        var films = new MediaProvider(WorkKind.Film, Title);
        var games = new MediaProvider(WorkKind.Game);

        var answer = await GroundAsync(SpineRead(WorkKind.Game), ScoringProfileKind.ShelfSpine, films, games);

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options[0].Provider!.Title.ShouldBe(Title);
        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Film);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Steps.ShouldContain(new TraceStep("anykind", $"{Title} (kind read: game)", 0));
        films.Asked.ShouldBe([(Title, null)]);
    }

    [Fact]
    public async Task A_game_read_whose_own_game_scores_above_what_any_kind_finds_keeps_its_game_on_top()
    {
        var films = new MediaProvider(WorkKind.Film, Title + " Returns Again Tonight");
        var games = new MediaProvider(WorkKind.Game, Title);
        var facts = SpineRead(WorkKind.Game) with { Platform = "Sega Saturn" };

        var answer = await GroundAsync(facts, ScoringProfileKind.ShelfSpine, films, games);

        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Game);
        answer.Options.Select(o => o.Provider!.Kind).ShouldNotContain(WorkKind.Film);
    }

    [Fact]
    public async Task A_spine_read_as_a_book_is_not_asked_as_any_kind()
    {
        var films = new MediaProvider(WorkKind.Film, Title);

        var answer = await GroundAsync(SpineRead(WorkKind.Book), ScoringProfileKind.ShelfSpine, films);

        answer.Steps.ShouldNotContain(step => step.Provider == "anykind");
        films.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cover_read_as_a_game_that_finds_nothing_is_not_asked_as_any_kind()
    {
        var films = new MediaProvider(WorkKind.Film, Title);

        var answer = await GroundAsync(SpineRead(WorkKind.Game), ScoringProfileKind.Cover, films, new MediaProvider(WorkKind.Game));

        answer.Steps.ShouldNotContain(step => step.Provider == "anykind");
        films.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_matched_game_read_is_not_asked_again()
    {
        var films = new MediaProvider(WorkKind.Film, Title);
        var games = new MediaProvider(WorkKind.Game, Title);

        var answer = await GroundAsync(SpineRead(WorkKind.Game) with { Platform = null }, ScoringProfileKind.ShelfSpine, films, games);

        answer.Steps.ShouldNotContain(step => step.Provider == "anykind");
        films.Asked.ShouldBeEmpty();
        games.Asked.Count.ShouldBe(1);
    }
}
