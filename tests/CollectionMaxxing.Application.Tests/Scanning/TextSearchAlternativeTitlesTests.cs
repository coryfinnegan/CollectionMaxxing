using System.Collections.Concurrent;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace CollectionMaxxing.Application.Tests.Scanning;

// #828: the film catalogue is asked for the alternative titles of the best few candidates that carry a read's leading word, only for a
// film or TV read that nothing matched, and a failed ask changes nothing. The catalogue here is a fake that counts what it is asked.
public class TextSearchAlternativeTitlesTests
{
    private const string BoxTitle = "HIGHLANDER THE FINAL DIMENSION";

    private static readonly ScoringProfile Spine = new ScoringOptions().Profile(ScoringProfileKind.ShelfSpine);

    private sealed class FakeCatalogue(Func<ProviderRelease, CancellationToken, Task<IReadOnlyList<string>?>> answer) : ICatalogProvider, IAlternativeTitleProvider
    {
        public ConcurrentQueue<string> Asked { get; } = new();

        public string Name => "fake";

        public bool Supports(WorkKind kind) => kind is WorkKind.Film or WorkKind.Tv;

        public bool HasAlternativeTitles(ProviderRelease release) => release.Provider == "fake" && release.Kind is WorkKind.Film or WorkKind.Tv;

        public Task<ProviderRelease?> LookupAsync(CollectionMaxxing.Domain.Identifiers.Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);

        public Task<IReadOnlyList<string>?> AlternativeTitlesAsync(ProviderRelease release, CancellationToken ct)
        {
            Asked.Enqueue(release.ExternalId);
            return answer(release, ct);
        }
    }

    private static TextSearchResolver Resolver(FakeCatalogue catalogue) =>
        new(null!, null!, [catalogue], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance);

    private static ProviderRelease Release(string id, string title, WorkKind kind = WorkKind.Film, int? year = null) => new()
    {
        Provider = "fake",
        ExternalId = id,
        Title = title,
        Kind = kind,
        Year = year,
    };

    private static ProviderAnswers Answers(params ProviderRelease[] found) => new(found, [], false);

    private static ReadItem Read(string title = BoxTitle, WorkKind? kind = WorkKind.Film) => new() { Title = title, Kind = kind };

    private static Task<ProviderAnswers> Ask(TextSearchResolver resolver, ReadItem read, ProviderAnswers answers, LocalPool? local = null, CancellationToken budget = default) =>
        resolver.WithAlternativeTitlesAsync(read, Spine, local ?? LocalPool.Empty, answers, CancellationToken.None, budget);

    private static Task<IReadOnlyList<string>?> Titles(params string[] titles) => Task.FromResult<IReadOnlyList<string>?>(titles);

    private static readonly ProviderRelease[] Highlanders =
    [
        Release("movie/1", "Highlander: Endgame", year: 2000),
        Release("movie/2", "Highlander II: The Quickening", year: 1991),
        Release("movie/3", "Highlander III: The Sorcerer", year: 1994),
    ];

    [Fact]
    public async Task A_film_read_by_its_alternative_title_is_matched_to_the_film_that_has_it()
    {
        var catalogue = new FakeCatalogue((release, _) => release.ExternalId == "movie/3" ? Titles("Highlander: The Final Dimension") : Titles("Unrelated"));
        var answers = await Ask(Resolver(catalogue), Read(), Answers(Highlanders));

        var result = TextSearchResolver.Score(Read(), Spine, LocalPool.Empty, answers, pendingWhenProvidersFailed: true);

        result.Verdict.ShouldBe(ResolutionVerdict.Matched);
        result.Options[0].Provider!.ExternalId.ShouldBe("movie/3");
    }

    [Fact]
    public async Task The_shown_title_stays_the_main_title()
    {
        var catalogue = new FakeCatalogue((release, _) => release.ExternalId == "movie/3" ? Titles("Highlander: The Final Dimension") : Titles());
        var answers = await Ask(Resolver(catalogue), Read(), Answers(Highlanders));

        var result = TextSearchResolver.Score(Read(), Spine, LocalPool.Empty, answers, pendingWhenProvidersFailed: true);

        result.Options[0].Provider!.Title.ShouldBe("Highlander III: The Sorcerer");
    }

    [Fact]
    public async Task A_read_that_is_already_matched_asks_for_no_alternative_titles()
    {
        var catalogue = new FakeCatalogue((_, _) => Titles("Anything"));
        var found = Answers(Release("movie/10", "Highlander", year: 1986), Release("movie/11", "Highlander: Endgame", year: 2000));

        var answers = await Ask(Resolver(catalogue), Read("Highlander"), found);

        catalogue.Asked.ShouldBeEmpty();
        answers.ShouldBeSameAs(found);
    }

    [Fact]
    public async Task Only_the_top_three_candidates_carrying_the_reads_leading_word_are_asked()
    {
        var catalogue = new FakeCatalogue((_, _) => Titles());
        var found = Answers(
            Release("movie/1", "The Final Dimension"), // scores best but lacks "Highlander"
            Release("movie/2", "Highlander: The Final Chapter"),
            Release("movie/3", "Highlander III: The Sorcerer"),
            Release("movie/4", "Highlander: Endgame"),
            Release("movie/5", "Highlander II: The Quickening"),
            Release("movie/6", "Highlander: The Source"));

        await Ask(Resolver(catalogue), Read(), found);

        catalogue.Asked.Count.ShouldBe(3);
        catalogue.Asked.ShouldNotContain("movie/1");
        var ranked = MatchScorer.Evaluate(Read(), [.. found.Found.Select(r => r.ToCandidate())], Spine).Ranked
            .Select(r => r.Release.Key.Replace("fake:", string.Empty)).Where(id => id != "movie/1").Take(3);
        catalogue.Asked.OrderBy(id => id).ShouldBe(ranked.OrderBy(id => id));
    }

    [Fact]
    public async Task A_candidate_without_the_reads_leading_word_is_never_asked()
    {
        var catalogue = new FakeCatalogue((_, _) => Titles());

        await Ask(Resolver(catalogue), Read(), Answers(Release("movie/1", "The Final Dimension"), Release("movie/2", "Dimension Jump")));

        catalogue.Asked.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(WorkKind.Book)]
    [InlineData(WorkKind.Game)]
    public async Task Books_and_games_never_ask(WorkKind kind)
    {
        var catalogue = new FakeCatalogue((_, _) => Titles("Highlander: The Final Dimension"));

        // A book or a game read, and book and game candidates a (hypothetical) provider could list titles for.
        await Ask(Resolver(catalogue), Read(kind: kind), Answers(Highlanders));
        await Ask(Resolver(catalogue), Read(kind: null), Answers([.. Highlanders.Select(r => r with { Kind = kind })]));

        catalogue.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tv_read_asks_too()
    {
        var catalogue = new FakeCatalogue((_, _) => Titles());

        await Ask(Resolver(catalogue), Read(kind: WorkKind.Tv), Answers(Release("tv/1", "Highlander: The Series", WorkKind.Tv)));
        await Ask(Resolver(catalogue), Read(kind: null), Answers(Release("tv/2", "Highlander: The Raven", WorkKind.Tv)));

        catalogue.Asked.OrderBy(id => id).ShouldBe(["tv/1", "tv/2"]);
    }

    [Fact]
    public async Task A_failed_alternative_titles_call_leaves_the_candidates_as_they_were()
    {
        var catalogue = new FakeCatalogue((release, _) => release.ExternalId switch
        {
            "movie/1" => Task.FromResult<IReadOnlyList<string>?>(null),
            "movie/2" => Task.FromException<IReadOnlyList<string>?>(new HttpRequestException("down")),
            _ => Titles(), // answered, with none
        });
        var found = Answers(Highlanders);

        var answers = await Ask(Resolver(catalogue), Read(), found);

        catalogue.Asked.Count.ShouldBe(3);
        answers.ShouldBeSameAs(found);
        answers.Found.ShouldAllBe(release => release.AlternativeTitles.Count == 0);
    }

    [Fact]
    public async Task One_failed_call_does_not_lose_the_titles_of_the_others()
    {
        var catalogue = new FakeCatalogue((release, _) => release.ExternalId == "movie/3"
            ? Titles("Highlander: The Final Dimension")
            : Task.FromException<IReadOnlyList<string>?>(new HttpRequestException("down")));

        var answers = await Ask(Resolver(catalogue), Read(), Answers(Highlanders));

        answers.Found.Single(r => r.ExternalId == "movie/3").AlternativeTitles.ShouldBe(["Highlander: The Final Dimension"]);
        answers.Found.Where(r => r.ExternalId != "movie/3").ShouldAllBe(r => r.AlternativeTitles.Count == 0);
        answers.AnyFailed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_slow_call_is_cut_off_by_the_budget_and_changes_nothing()
    {
        var catalogue = new FakeCatalogue(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var found = Answers(Highlanders);

        var answers = await Ask(Resolver(catalogue), Read(), found, budget: budget.Token);

        answers.ShouldBeSameAs(found);
    }

    [Fact]
    public async Task Nothing_is_asked_once_the_budget_is_spent()
    {
        var catalogue = new FakeCatalogue((_, _) => Titles());
        using var budget = new CancellationTokenSource();
        await budget.CancelAsync();

        await Ask(Resolver(catalogue), Read(), Answers(Highlanders), budget: budget.Token);

        catalogue.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_calls_run_at_the_same_time_not_one_after_the_other()
    {
        var inside = 0;
        var most = 0;
        var catalogue = new FakeCatalogue(async (_, ct) =>
        {
            var now = Interlocked.Increment(ref inside);
            int seen;
            while (now > (seen = Volatile.Read(ref most)) && Interlocked.CompareExchange(ref most, now, seen) != seen)
            {
            }

            await Task.Delay(200, ct);
            Interlocked.Decrement(ref inside);
            return (IReadOnlyList<string>?)[];
        });

        await Ask(Resolver(catalogue), Read(), Answers(Highlanders));

        most.ShouldBe(3, "all three calls were inside the catalogue at once");
    }

    [Fact]
    public async Task The_call_is_traced_without_a_title_in_it()
    {
        var catalogue = new FakeCatalogue((release, _) => release.ExternalId == "movie/3" ? Titles("Highlander: The Final Dimension") : Titles());

        var answers = await Ask(Resolver(catalogue), Read(), Answers(Highlanders));

        var step = answers.Steps.ShouldHaveSingleItem();
        step.Provider.ShouldBe("alternative_titles");
        step.Outcome.ShouldNotContain("Highlander");
    }

    // ---- what "already matched" means: the whole pool, under the read's own profile --------------------------------------------

    private static LocalPool Local(params CandidateRelease[] candidates) =>
        new([.. candidates.Select(c => (c, new ResolutionOption(Guid.NewGuid(), null, 0)))], null, new Dictionary<string, Guid>());

    private static CandidateRelease LocalFilm(string key, string title, int year) => new() { Key = key, Title = title, Kind = WorkKind.Film, Year = year };

    [Fact]
    public async Task A_film_matched_by_its_own_title_stays_matched_when_another_film_lists_the_read_as_an_alternative_title()
    {
        var catalogue = new FakeCatalogue((release, _) => release.ExternalId == "movie/1" ? Titles("The Sorcerer") : Titles());
        var found = Answers(Release("movie/2", "The Sorcerer", year: 1994), Release("movie/1", "Highlander III", year: 1994));

        var answers = await Ask(Resolver(catalogue), Read("The Sorcerer"), found);
        var result = TextSearchResolver.Score(Read("The Sorcerer"), Spine, LocalPool.Empty, answers, pendingWhenProvidersFailed: true);

        catalogue.Asked.ShouldBeEmpty();
        result.Verdict.ShouldBe(ResolutionVerdict.Matched);
        result.Options[0].Provider!.ExternalId.ShouldBe("movie/2");
    }

    [Fact]
    public async Task A_read_the_local_catalogue_matches_by_the_films_own_title_asks_for_no_alternative_titles()
    {
        var catalogue = new FakeCatalogue((_, _) => Titles("The Sorcerer"));
        var found = Answers(Release("movie/1", "Highlander III", year: 1994));

        var answers = await Ask(Resolver(catalogue), Read("The Sorcerer"), found, local: Local(LocalFilm("local:1", "The Sorcerer", 1994)));

        catalogue.Asked.ShouldBeEmpty();
        answers.ShouldBeSameAs(found);
    }

    [Fact]
    public async Task A_read_is_judged_under_the_profile_it_is_scored_with()
    {
        var found = Answers(Release("movie/1", "Highlander", year: 1986), Release("movie/2", "Highlander: Endgame", year: 2000));
        var strict = new ScoringProfile("strict", Spine.Weights, new ScoringThresholds { MatchedScore = 1.01 });

        var underItsOwn = new FakeCatalogue((_, _) => Titles());
        await Resolver(underItsOwn).WithAlternativeTitlesAsync(Read("Highlander"), Spine, LocalPool.Empty, found, CancellationToken.None, CancellationToken.None);
        var underOther = new FakeCatalogue((_, _) => Titles());
        await Resolver(underOther).WithAlternativeTitlesAsync(Read("Highlander"), strict, LocalPool.Empty, found, CancellationToken.None, CancellationToken.None);

        underItsOwn.Asked.ShouldBeEmpty("matched under the profile the read gets");
        underOther.Asked.ShouldNotBeEmpty("not matched under a stricter profile");
    }

    [Fact]
    public async Task A_film_already_in_the_local_catalogue_takes_the_alternative_titles_of_its_provider_copy()
    {
        var catalogue = new FakeCatalogue((release, _) => release.ExternalId == "movie/3" ? Titles("Highlander: The Final Dimension") : Titles());
        var local = Local(LocalFilm("local:3", "Highlander III: The Sorcerer", 1994));

        var answers = await Ask(Resolver(catalogue), Read(), Answers(Highlanders), local: local);
        var result = TextSearchResolver.Score(Read(), Spine, local, answers, pendingWhenProvidersFailed: true);

        catalogue.Asked.Count.ShouldBe(3);
        result.Verdict.ShouldBe(ResolutionVerdict.Matched);
        result.Options[0].LocalReleaseId.ShouldNotBeNull("the catalogue's own copy is the match");
        result.Options[0].Provider.ShouldBeNull();
    }

    [Fact]
    public async Task A_manual_search_never_asks_for_alternative_titles()
    {
        var catalogue = new FakeCatalogue((_, _) => Titles("Highlander: The Final Dimension"));
        var searching = new FilmSearcher(Highlanders, catalogue);
        var resolver = new TextSearchResolver(null!, new EmptyLocal(), [searching], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance);

        var result = await resolver.SearchAsync(BoxTitle, CancellationToken.None);

        searching.Searched.ShouldBeGreaterThan(0);
        catalogue.Asked.ShouldBeEmpty();
        result.Verdict.ShouldNotBe(ResolutionVerdict.Matched);
    }

    private sealed class EmptyLocal : ILocalCatalogSearch
    {
        public Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalHit>>([]);
    }

    /// <summary>A catalogue that finds the films for any search and lists their titles through <paramref name="titles"/>.</summary>
    private sealed class FilmSearcher(IReadOnlyList<ProviderRelease> films, FakeCatalogue titles) : ICatalogProvider, IAlternativeTitleProvider
    {
        public int Searched { get; private set; }

        public string Name => "fake";

        public bool Supports(WorkKind kind) => kind is WorkKind.Film or WorkKind.Tv;

        public bool HasAlternativeTitles(ProviderRelease release) => titles.HasAlternativeTitles(release);

        public Task<ProviderRelease?> LookupAsync(CollectionMaxxing.Domain.Identifiers.Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            Searched++;
            return Task.FromResult(films);
        }

        public Task<IReadOnlyList<string>?> AlternativeTitlesAsync(ProviderRelease release, CancellationToken ct) => titles.AlternativeTitlesAsync(release, ct);
    }

    // ---- who asks: the server's own reads only ---------------------------------------------------------------------------------

    private sealed class FixedScoring : Microsoft.Extensions.Options.IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue { get; } = new();

        public ScoringOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ScoringOptions, string?> listener) => null;
    }

    private static CollectionMaxxing.Application.Scanning.Identify.ReadFacts Facts(bool serverRead) =>
        new(BoxTitle, null, null, null, null, WorkKind.Film, null, null, null, ServerRead: serverRead);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public async Task Only_the_servers_own_reads_ask_for_alternative_titles(bool serverRead, int asked)
    {
        var catalogue = new FakeCatalogue((_, _) => Titles());
        var grounding = new CollectionMaxxing.Application.Scanning.Identify.IdentifyGrounding(Resolver(catalogue), new FixedScoring());

        await grounding.WithAlternativeTitlesAsync(
            Facts(serverRead), ScoringProfileKind.ShelfSpine, LocalPool.Empty, Answers(Highlanders), CancellationToken.None, CancellationToken.None);

        catalogue.Asked.Count.ShouldBe(asked);
    }

    [Fact]
    public async Task Grounding_a_read_on_its_own_never_asks_for_alternative_titles_whoever_made_it()
    {
        // The phone's identify and the background job ground a read with GroundAsync: neither asks, server mark or not.
        var asked = new FakeCatalogue((_, _) => Titles("Highlander: The Final Dimension"));
        var grounding = new CollectionMaxxing.Application.Scanning.Identify.IdentifyGrounding(
            new TextSearchResolver(null!, new EmptyLocal(), [new FilmSearcher(Highlanders, asked)], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
            new FixedScoring());

        await grounding.GroundAsync(Facts(serverRead: true), ScoringProfileKind.ShelfSpine, TimeSpan.FromSeconds(30), CancellationToken.None);
        await grounding.GroundAsync(Facts(serverRead: false), ScoringProfileKind.ShelfSpine, TimeSpan.FromSeconds(30), CancellationToken.None);

        asked.Asked.ShouldBeEmpty();
    }
}
