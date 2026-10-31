using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Evals;

/// <summary>
/// The time an eval read has to be answered in. The providers pace themselves with one limiter each, shared by every ask, so with
/// K reads in flight every call waits up to K times longer than it would alone; the budget of an ask grows with them, so a read
/// the sequential grounder answers is answered with the same verdict however many are asked at once. All on a fake clock, with the
/// real limiter the provider adapters use.
/// </summary>
public class EvalGroundingBudgetTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    private const int MostSteps = 20_000;

    private static readonly string[] SixWordTitles =
    [
        "Amber Birch Cedar Dune Elm Fir",
        "Basil Cove Dusk Ember Flint Grove",
        "Cobalt Dawn Easel Fjord Glade Haze",
        "Dapple Eddy Fable Gorse Heath Inlet",
        "Elder Frost Gable Hollow Ivory Jade",
        "Fennel Gust Harbor Iris Juniper Knoll",
    ];

    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    private sealed class FixedScoring : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue { get; } = new();

        public ScoringOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ScoringOptions, string?> listener) => null;
    }

    private sealed class EmptyCatalogue : ILocalCatalogSearch
    {
        public Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalHit>>([]);
    }

    /// <summary>Spends one slot of a shared limiter on every search, as the provider adapters do, and finds nothing, so a ladder runs to its last rung.</summary>
    private sealed class LimitedProvider(TokenBucketRateLimiter limiter) : ICatalogProvider
    {
        private int _searches;

        public int Searches => Volatile.Read(ref _searches);

        public string Name => "limited";

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            await limiter.WaitAsync(ct);
            Interlocked.Increment(ref _searches);
            return [];
        }
    }

    /// <summary>Takes <c>Quick</c> titles six seconds, <c>Slow</c> titles forever, both on the fake clock and both until the ask's budget runs out.</summary>
    private sealed class TimedProvider(TimeProvider time) : ICatalogProvider
    {
        public string Name => "timed";

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            await Task.Delay(query.Title.Contains("Slow", StringComparison.Ordinal) ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(6), time, ct);
            return [new ProviderRelease { Provider = "timed", ExternalId = query.Title, Title = query.Title, Kind = WorkKind.Game, Format = ReleaseFormat.GameCartridge, Platform = "Nintendo 64", Year = 1997 }];
        }
    }

    private static EvalReadItemInput Read(string title) => new(
        new IdentifyItemInput(null, null, title, title, null, null, null, WorkKind.Game, ReleaseFormat.GameCartridge, null, "Nintendo 64", null),
        null);

    private static EvalGrounder Grounder(FakeTimeProvider time, ICatalogProvider provider, int parallelism, TimeSpan budget)
    {
        var scanning = new ScanningOptions { JobResolveBudget = budget };
        var resolver = new TextSearchResolver(null!, new EmptyCatalogue(), [provider], scanning, NullLogger<TextSearchResolver>.Instance);
        var grounding = new IdentifyGrounding(resolver, new FixedScoring());
        return new EvalGrounder(
            grounding, null!, scanning, Options.Create(new EvalsOptions { GroundingParallelism = parallelism }), new NoSerials(), time);
    }

    /// <summary>Grounds the titles and moves the fake clock along until it is done, failing if it never is.</summary>
    private static async Task<string[]> VerdictsAsync(FakeTimeProvider time, EvalGrounder grounder, params string[] titles)
    {
        var grounding = grounder.GroundAsync(ScanEngine.VisionPipeline, [.. titles.Select(Read)], new EvalGroundingMemo(), CancellationToken.None);
        for (var step = 0; !grounding.IsCompleted; step++)
        {
            step.ShouldBeLessThan(MostSteps, "the grounding never finished");
            time.Advance(Step);
            await Task.Delay(1);
        }

        return [.. (await grounding).Select(i => i.Verdict)];
    }

    /// <summary>How many searches a read's ladder makes when nothing is close, and the verdict it ends with when it has time for them all.</summary>
    private static async Task<(int Searches, string Verdict)> LadderAsync(string title)
    {
        var time = new FakeTimeProvider();
        var provider = new LimitedProvider(TokenBucketRateLimiter.PerSecond(1, time));

        var verdicts = await VerdictsAsync(time, Grounder(time, provider, parallelism: 1, TimeSpan.FromHours(1)), title);

        return (provider.Searches, verdicts.Single());
    }

    [Fact]
    public async Task A_read_whose_ladder_needs_the_whole_sequential_budget_gets_the_same_verdict_with_six_reads_in_flight()
    {
        var (searches, sequential) = await LadderAsync(SixWordTitles[0]);
        searches.ShouldBeGreaterThan(2);
        sequential.ShouldBe("not_found");

        // The sequential grounder makes its last search at (searches - 1) seconds on a limiter of one a second: this budget is just enough for it.
        var budget = TimeSpan.FromSeconds(searches);
        var time = new FakeTimeProvider();
        var alone = new LimitedProvider(TokenBucketRateLimiter.PerSecond(1, time));
        (await VerdictsAsync(time, Grounder(time, alone, parallelism: 1, budget), SixWordTitles[0])).ShouldBe([sequential]);

        // Six reads share the limiter, so each search waits for the other five: every ladder takes about six times as long.
        var shared = new FakeTimeProvider();
        var provider = new LimitedProvider(TokenBucketRateLimiter.PerSecond(1, shared));
        var verdicts = await VerdictsAsync(shared, Grounder(shared, provider, parallelism: 6, budget), SixWordTitles);

        verdicts.ShouldBe(Enumerable.Repeat(sequential, SixWordTitles.Length));
        provider.Searches.ShouldBe(searches * SixWordTitles.Length);
    }

    [Fact]
    public async Task A_read_asked_on_its_own_does_not_get_more_time_for_reads_that_are_not_there()
    {
        var (searches, _) = await LadderAsync(SixWordTitles[0]);
        var time = new FakeTimeProvider();
        var provider = new LimitedProvider(TokenBucketRateLimiter.PerSecond(1, time));
        var tooShort = TimeSpan.FromSeconds(searches / 2.0);

        var verdicts = await VerdictsAsync(time, Grounder(time, provider, parallelism: 6, tooShort), SixWordTitles[0]);

        verdicts.ShouldBe(["pending"]);
    }

    [Fact]
    public async Task A_read_queued_behind_a_slow_one_has_its_own_budget_counted_from_when_its_ask_starts()
    {
        // One place: the slow read holds it for its whole budget (10 s), and the quick read, which needs 6 s, can only start after that.
        // Had its budget been counted from the start of the call, it would already be spent and the quick read would be pending.
        var time = new FakeTimeProvider();
        var grounder = Grounder(time, new TimedProvider(time), parallelism: 1, TimeSpan.FromSeconds(10));

        var verdicts = await VerdictsAsync(time, grounder, "Slow Poke Quest", "Quick Quest");

        verdicts.ShouldBe(["pending", "matched"]);
    }
}
