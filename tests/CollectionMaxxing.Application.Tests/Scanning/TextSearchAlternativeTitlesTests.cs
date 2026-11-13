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

    private static Task<ProviderAnswers> Ask(TextSearchResolver resolver, ReadItem read, ProviderAnswers answers, CancellationToken budget = default) =>
        resolver.WithAlternativeTitlesAsync(read, [Spine], answers, CancellationToken.None, budget);

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
    public async Task With_several_profiles_the_read_counts_as_matched_only_when_every_profile_says_so()
    {
        // The caller cannot always say which profile the read gets: with two, it is asked unless every one says matched.
        var catalogue = new FakeCatalogue((_, _) => Titles());
        var loose = new ScoringProfile("loose", Spine.Weights, new ScoringThresholds { MatchedScore = 0.1 });
        var found = Answers(Highlanders);

        await Resolver(catalogue).WithAlternativeTitlesAsync(Read(), [loose, Spine], found, CancellationToken.None, CancellationToken.None);
        catalogue.Asked.Count.ShouldBe(3);

        var onlyLoose = new FakeCatalogue((_, _) => Titles());
        await Resolver(onlyLoose).WithAlternativeTitlesAsync(Read(), [loose], found, CancellationToken.None, CancellationToken.None);
        onlyLoose.Asked.ShouldBeEmpty();
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

        var answers = await Ask(Resolver(catalogue), Read(), found, budget.Token);

        answers.ShouldBeSameAs(found);
    }

    [Fact]
    public async Task Nothing_is_asked_once_the_budget_is_spent()
    {
        var catalogue = new FakeCatalogue((_, _) => Titles());
        using var budget = new CancellationTokenSource();
        await budget.CancelAsync();

        await Ask(Resolver(catalogue), Read(), Answers(Highlanders), budget.Token);

        catalogue.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_calls_run_at_the_same_time_not_one_after_the_other()
    {
        var started = 0;
        var allStarted = new TaskCompletionSource();
        var catalogue = new FakeCatalogue(async (_, ct) =>
        {
            if (Interlocked.Increment(ref started) == 3)
            {
                allStarted.SetResult();
            }

            // Each call waits for the other two: one after the other, this never finishes.
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return (IReadOnlyList<string>?)[];
        });

        await Ask(Resolver(catalogue), Read(), Answers(Highlanders));

        started.ShouldBe(3);
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
}
