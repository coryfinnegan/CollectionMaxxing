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
// the find scores strictly higher. Only a spine on a shelf is asked this, only a read that names a game, and only after the
// read's other attempts.
public class IdentifyGroundingAnyKindRetryTests
{
    private const string Title = "Harbour Lights";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static ReadFacts SpineRead(WorkKind kind, string title = Title) => kind == WorkKind.Game
        ? new(title, title, null, null, null, kind, ReleaseFormat.GameDisc, null, "PlayStation")
        : new(title, title, null, null, null, kind, ReleaseFormat.Vhs, null, null);

    /// <summary>A provider of one kind that finds the releases titled as asked (or every release it knows, when told), and remembers its queries.</summary>
    private sealed class MediaProvider(WorkKind kind, string platform, bool findsAnyTitle, params string[] titles) : ICatalogProvider
    {
        public List<CatalogQuery> Asked { get; } = [];

        /// <summary>Finds nothing for a query that names no kind (as a provider that is only searched by its own kind).</summary>
        public bool NeedsKind { get; init; }

        public string Name => kind switch { WorkKind.Film => "tmdb", WorkKind.Book => "openlibrary", _ => "igdb" };

        public bool Supports(WorkKind asked) => asked == kind;

        public Task<ProviderRelease?> LookupAsync(CollectionMaxxing.Domain.Identifiers.Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            Asked.Add(query);
            return Task.FromResult<IReadOnlyList<ProviderRelease>>([.. titles
                .Select((t, n) => (Title: t, Index: n))
                .Where(_ => !NeedsKind || query.Kind is not null)
                .Where(t => findsAnyTitle || string.Equals(t.Title, query.Title, StringComparison.OrdinalIgnoreCase))
                .Select(t => new ProviderRelease
                {
                    Provider = Name,
                    ExternalId = $"{Name}-{t.Index}",
                    Title = t.Title,
                    Kind = kind,
                    Format = kind == WorkKind.Film ? ReleaseFormat.Dvd : ReleaseFormat.GameDisc,
                    Platform = kind == WorkKind.Game ? platform : null,
                    Year = 1999 + t.Index,
                })]);
        }
    }

    private static MediaProvider Films(params string[] titles) => new(WorkKind.Film, string.Empty, false, titles);

    private static MediaProvider Games(string platform, params string[] titles) => new(WorkKind.Game, platform, false, titles);

    private static Task<BarcodeResolution> GroundAsync(ReadFacts facts, ScoringProfileKind profile, params MediaProvider[] providers)
    {
        var grounding = new IdentifyGrounding(
            new TextSearchResolver(null!, new Catalogue(), providers, new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
            new FixedScoring());
        return grounding.GroundAsync(facts, profile, Budget, CancellationToken.None);
    }

    private static string Trace(BarcodeResolution answer) => string.Join(" > ", answer.Steps.Select(s => $"{s.Provider}:{s.Outcome}")) + " | " + answer.Verdict;

    private static bool Asked(BarcodeResolution answer) => answer.Steps.Any(step => step.Provider == "anykind");

    [Fact]
    public async Task A_spine_read_as_a_game_that_finds_nothing_is_asked_as_any_kind_and_the_film_it_finds_is_offered()
    {
        var films = Films(Title);

        var answer = await GroundAsync(SpineRead(WorkKind.Game), ScoringProfileKind.ShelfSpine, films, Games("PlayStation"));

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Film);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Steps.ShouldContain(new TraceStep("anykind", $"{Title} (kind read: game)", 0));
    }

    [Fact]
    public async Task A_spine_read_as_a_game_that_finds_only_a_game_on_another_platform_is_asked_as_any_kind_and_the_film_it_finds_goes_first()
    {
        var films = Films(Title);
        var games = Games("PC", Title);

        var answer = await GroundAsync(SpineRead(WorkKind.Game), ScoringProfileKind.ShelfSpine, films, games);

        Asked(answer).ShouldBeTrue(Trace(answer));
        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Film);
        answer.Options.Select(o => o.Provider!.Kind).ShouldContain(WorkKind.Game);
    }

    [Fact]
    public async Task The_any_kind_ask_has_no_kind_no_platform_and_no_format()
    {
        var films = Films(Title);
        var games = Games("PC", Title);

        await GroundAsync(SpineRead(WorkKind.Game), ScoringProfileKind.ShelfSpine, films, games);

        games.Asked[0].Kind.ShouldBe(WorkKind.Game);
        games.Asked[0].Platform.ShouldBe("PlayStation");
        games.Asked[0].Format.ShouldBe(ReleaseFormat.GameDisc);
        var anyKind = films.Asked.ShouldHaveSingleItem();
        anyKind.Kind.ShouldBeNull();
        anyKind.Platform.ShouldBeNull();
        anyKind.Format.ShouldBeNull();
    }

    [Fact]
    public async Task An_ambiguous_game_read_whose_own_best_scores_above_what_any_kind_finds_keeps_its_game_on_top()
    {
        // The game is the title on another platform (capped, Ambiguous); the film any kind finds is a different, weaker title.
        var films = new MediaProvider(WorkKind.Film, string.Empty, true, Title + " Returns");
        var games = new MediaProvider(WorkKind.Game, "PC", false, Title) { NeedsKind = true };

        var answer = await GroundAsync(SpineRead(WorkKind.Game), ScoringProfileKind.ShelfSpine, films, games);

        Asked(answer).ShouldBeTrue(Trace(answer));
        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Game);
        answer.Options.Select(o => o.Provider!.Kind).ShouldContain(WorkKind.Film);
        answer.FoundByTrimmedSpan.ShouldBeFalse(Trace(answer));
    }

    [Fact]
    public async Task An_any_kind_find_that_only_ties_the_game_reads_own_best_does_not_go_above_it()
    {
        // Two games of the title on the read's platform (an original and its remake) tie with the same-titled film at 1.0.
        var films = Films(Title);
        var games = Games("PlayStation", Title, Title);

        var answer = await GroundAsync(SpineRead(WorkKind.Game), ScoringProfileKind.ShelfSpine, films, games);

        Asked(answer).ShouldBeTrue(Trace(answer));
        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Game);
        answer.FoundByTrimmedSpan.ShouldBeFalse(Trace(answer));
    }

    [Fact]
    public async Task The_any_kind_ask_comes_after_the_reads_other_attempts()
    {
        // The title without its possessive name finds the game; any kind, asked first, would have found the film of the full title.
        var films = Films("Walt Disney's Harbour Lights");
        var games = Games("PlayStation", Title);

        var answer = await GroundAsync(SpineRead(WorkKind.Game, "Walt Disney's Harbour Lights"), ScoringProfileKind.ShelfSpine, films, games);

        Asked(answer).ShouldBeFalse(Trace(answer));
        answer.Steps.ShouldContain(step => step.Provider == "possessive");
        films.Asked.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(WorkKind.Book)]
    [InlineData(WorkKind.Film)]
    [InlineData(WorkKind.Tv)]
    public async Task A_spine_read_as_another_kind_than_a_game_is_not_asked_as_any_kind(WorkKind kind)
    {
        var answer = await GroundAsync(SpineRead(kind), ScoringProfileKind.ShelfSpine, Films(Title), Games("PlayStation", Title));

        Asked(answer).ShouldBeFalse(Trace(answer));
    }

    [Fact]
    public async Task A_cover_read_as_a_game_that_finds_nothing_is_not_asked_as_any_kind()
    {
        var films = Films(Title);

        var answer = await GroundAsync(SpineRead(WorkKind.Game), ScoringProfileKind.Cover, films, Games("PlayStation"));

        Asked(answer).ShouldBeFalse(Trace(answer));
        films.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_matched_game_read_is_not_asked_again()
    {
        var films = Films(Title);
        var games = Games("PlayStation", Title);

        var answer = await GroundAsync(SpineRead(WorkKind.Game), ScoringProfileKind.ShelfSpine, films, games);

        Asked(answer).ShouldBeFalse(Trace(answer));
        films.Asked.ShouldBeEmpty();
        games.Asked.Count.ShouldBe(1);
    }

    /// <summary>Books of the titles asked, a year later than a film of the same title (so the two are not one work).</summary>
    private static MediaProvider Books(params string[] titles) => new(WorkKind.Book, string.Empty, false, [.. titles.Prepend("A different book")]);

    private static ReadFacts GameReadOnASnapOf(WorkKind? snapKind) => SpineRead(WorkKind.Game) with { KindContext = snapKind };

    [Fact]
    public async Task A_game_read_asked_as_any_kind_on_a_snap_with_no_kind_puts_the_film_first_among_releases_of_the_same_score()
    {
        var answer = await GroundAsync(GameReadOnASnapOf(null), ScoringProfileKind.ShelfSpine, Books(Title), Films(Title), Games("PlayStation"));

        Asked(answer).ShouldBeTrue(Trace(answer));
        answer.Options.Select(o => o.Provider!.Kind).ShouldContain(WorkKind.Book);
        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Film);
    }

    [Fact]
    public async Task A_game_read_asked_as_any_kind_on_a_snap_of_books_keeps_the_snaps_kind()
    {
        var answer = await GroundAsync(GameReadOnASnapOf(WorkKind.Book), ScoringProfileKind.ShelfSpine, Books(Title), Films(Title), Games("PlayStation"));

        Asked(answer).ShouldBeTrue(Trace(answer));
        answer.Options.Select(o => o.Provider!.Kind).ShouldContain(WorkKind.Film);
        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Book);
    }

    [Theory]
    [InlineData(null, WorkKind.Film)]
    [InlineData(WorkKind.Film, WorkKind.Film)]
    [InlineData(WorkKind.Book, WorkKind.Book)]
    public async Task The_any_kind_ask_of_a_game_read_carries_the_film_context_only_when_the_snap_has_none(WorkKind? snapKind, WorkKind first)
    {
        var answer = await GroundAsync(GameReadOnASnapOf(snapKind), ScoringProfileKind.ShelfSpine, Books(Title), Films(Title), Games("PlayStation"));

        answer.Options[0].Provider!.Kind.ShouldBe(first, Trace(answer));
    }

    [Fact]
    public async Task The_film_context_of_the_any_kind_ask_is_not_part_of_the_ask()
    {
        var films = Films(Title);

        await GroundAsync(GameReadOnASnapOf(null), ScoringProfileKind.ShelfSpine, films, Games("PlayStation"));

        var anyKind = films.Asked.ShouldHaveSingleItem();
        anyKind.Title.ShouldBe(Title);
        anyKind.Kind.ShouldBeNull();
        anyKind.Platform.ShouldBeNull();
        anyKind.Format.ShouldBeNull();
    }
}
