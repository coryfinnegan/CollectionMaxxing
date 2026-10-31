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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>
/// The eval grounder asks the reads of one photo at the same time, the way <c>/identify</c> does: the catalogue once for all of
/// them, the providers in parallel up to a bound, each ask with the patient budget, nothing kept from an ask that was cut off.
/// </summary>
public class EvalGrounderConcurrencyTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly string[] Titles =
        ["Alpha Quest", "Beta Quest", "Gamma Quest", "Delta Quest", "Epsilon Quest", "Zeta Quest", "Eta Quest", "Theta Quest"];

    private static EvalReadItemInput Read(string? title) => new(
        new IdentifyItemInput(null, null, title, title, null, null, null, WorkKind.Game, ReleaseFormat.GameCartridge, null, "Nintendo 64", null),
        null);

    private static EvalReadItemInput[] Reads(params string?[] titles) => [.. titles.Select(Read)];

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

    /// <summary>A catalogue that holds nothing and remembers how it was asked: how often, for how many titles, and whether two asks overlapped.</summary>
    private sealed class RecordingCatalogue : ILocalCatalogSearch
    {
        private int _inFlight;

        public int OneAtATimeCalls { get; private set; }

        public List<IReadOnlyList<string>> Batches { get; } = [];

        public int MostAtOnce { get; private set; }

        public Exception? FailOnce { get; set; }

        public Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken)
        {
            OneAtATimeCalls++;
            return Task.FromResult<IReadOnlyList<LocalHit>>([]);
        }

        async Task<IReadOnlyList<IReadOnlyList<LocalHit>>> ILocalCatalogSearch.FindManyAsync(
            IReadOnlyList<LocalSearch> searches, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _inFlight);
            try
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
                Batches.Add([.. searches.Select(s => s.NormalizedTitle)]);
                await Task.Delay(10, cancellationToken);
                if (FailOnce is { } failure)
                {
                    FailOnce = null;
                    throw failure;
                }

                return [.. searches.Select(_ => (IReadOnlyList<LocalHit>)[])];
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>
    /// A provider that finds every title it is asked for, but only once <c>release</c> asks are waiting at the same time, so a grounder
    /// that asks fewer at once is seen waiting, and one that asks more is seen exceeding the bound. With a gate, it holds every ask until the test opens it.
    /// </summary>
    private sealed class WaitingProvider(int release, Task? gate = null) : ICatalogProvider
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private int _inFlight;
        private int _asks;
        private int _cancelled;
        private int _patient = 1;

        public string Name => "waiting";

        public int MostAtOnce { get; private set; }

        public int Asks => Volatile.Read(ref _asks);

        public int Cancelled => Volatile.Read(ref _cancelled);

        public bool AskedPatiently => Volatile.Read(ref _patient) == 1;

        public Task FirstAsk => _firstAsked.Task;

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref _asks);
            if (ProviderCalls.IsFailFast)
            {
                Interlocked.Exchange(ref _patient, 0);
            }

            var now = Interlocked.Increment(ref _inFlight);
            lock (_lock)
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
            }

            _firstAsked.TrySetResult();
            if (now >= release)
            {
                _reached.TrySetResult();
            }

            try
            {
                await _reached.Task.WaitAsync(Patience, ct);
                if (gate is not null)
                {
                    await gate.WaitAsync(ct);
                }

                await Task.Delay(15, ct);
                return [new ProviderRelease { Provider = "waiting", ExternalId = query.Title, Title = query.Title, Kind = WorkKind.Game, Format = ReleaseFormat.GameCartridge, Platform = "Nintendo 64", Year = 1997 }];
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>A provider that finds nothing for a title with "Slow" in it until it is cancelled, and everything else at once.</summary>
    private sealed class SlowForSomeProvider : ICatalogProvider
    {
        public string Name => "slow";

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            if (query.Title.Contains("Slow", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return [new ProviderRelease { Provider = "slow", ExternalId = query.Title, Title = query.Title, Kind = WorkKind.Game, Format = ReleaseFormat.GameCartridge, Platform = "Nintendo 64", Year = 1997 }];
        }
    }

    private static EvalGrounder Grounder(
        ILocalCatalogSearch catalogue, ICatalogProvider provider, int? parallelism = null, TimeSpan? budget = null)
    {
        var scanning = new ScanningOptions { JobResolveBudget = budget ?? TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, catalogue, [provider], scanning, NullLogger<TextSearchResolver>.Instance);
        var grounding = new IdentifyGrounding(resolver, new FixedScoring());
        var evals = Options.Create(parallelism is { } bound ? new EvalsOptions { GroundingParallelism = bound } : new EvalsOptions());
        return new EvalGrounder(grounding, null!, scanning, evals, new NoSerials());
    }

    private static Task<IReadOnlyList<GroundedItem>> GroundAsync(
        EvalGrounder grounder, EvalGroundingMemo memo, EvalReadItemInput[] reads, CancellationToken cancellationToken = default) =>
        grounder.GroundAsync(ScanEngine.VisionPipeline, reads, memo, cancellationToken);

    [Fact]
    public void Six_reads_are_asked_at_a_time_unless_the_settings_say_otherwise()
    {
        new EvalsOptions().GroundingParallelism.ShouldBe(6);
    }

    [Fact]
    public async Task Several_reads_of_one_photo_are_asked_at_the_same_time_up_to_the_bound_and_never_more()
    {
        var provider = new WaitingProvider(release: 3);
        var grounder = Grounder(new RecordingCatalogue(), provider, parallelism: 3);

        var items = await GroundAsync(grounder, new EvalGroundingMemo(), Reads(Titles)).WaitAsync(Patience);

        provider.MostAtOnce.ShouldBe(3);
        provider.Asks.ShouldBe(Titles.Length);
        items.Select(i => i.Verdict).ShouldAllBe(v => v == "matched");
    }

    [Fact]
    public async Task A_bound_of_nothing_asks_one_read_at_a_time()
    {
        var provider = new WaitingProvider(release: 1);
        var grounder = Grounder(new RecordingCatalogue(), provider, parallelism: 0);

        await GroundAsync(grounder, new EvalGroundingMemo(), Reads(Titles)).WaitAsync(Patience);

        provider.MostAtOnce.ShouldBe(1);
        provider.Asks.ShouldBe(Titles.Length);
    }

    [Fact]
    public async Task The_answers_come_back_in_the_order_of_the_reads_whatever_order_the_providers_answered_in()
    {
        var provider = new WaitingProvider(release: 4);
        var grounder = Grounder(new RecordingCatalogue(), provider, parallelism: 8);

        var items = await GroundAsync(grounder, new EvalGroundingMemo(), Reads(Titles)).WaitAsync(Patience);

        items.Select(i => i.ReadTitle).ShouldBe(Titles);
    }

    [Fact]
    public async Task The_catalogue_is_asked_once_for_every_read_of_the_call_and_never_twice_at_a_time()
    {
        var catalogue = new RecordingCatalogue();
        var grounder = Grounder(catalogue, new WaitingProvider(release: 1), parallelism: 4);

        await GroundAsync(grounder, new EvalGroundingMemo(), Reads([.. Titles, null, "  "])).WaitAsync(Patience);

        catalogue.Batches.Count.ShouldBe(1);
        catalogue.Batches[0].Count.ShouldBe(Titles.Length);
        catalogue.OneAtATimeCalls.ShouldBe(0);
        catalogue.MostAtOnce.ShouldBe(1);
    }

    [Fact]
    public async Task A_second_photo_with_the_same_reads_asks_neither_the_catalogue_nor_the_providers()
    {
        var catalogue = new RecordingCatalogue();
        var provider = new WaitingProvider(release: 1);
        var grounder = Grounder(catalogue, provider);
        var memo = new EvalGroundingMemo();

        await GroundAsync(grounder, memo, Reads(Titles)).WaitAsync(Patience);
        await GroundAsync(grounder, memo, Reads(Titles)).WaitAsync(Patience);

        catalogue.Batches.Count.ShouldBe(1);
        provider.Asks.ShouldBe(Titles.Length);
        memo.Grounded.ShouldBe(Titles.Length);
        memo.Reused.ShouldBe(Titles.Length);
    }

    [Fact]
    public async Task Identical_reads_of_one_photo_reach_the_provider_once()
    {
        var catalogue = new RecordingCatalogue();
        var provider = new WaitingProvider(release: 2);
        var grounder = Grounder(catalogue, provider);
        var memo = new EvalGroundingMemo();

        var items = await GroundAsync(grounder, memo, Reads("Alpha Quest", "Beta Quest", "Alpha Quest", "Alpha Quest")).WaitAsync(Patience);

        provider.Asks.ShouldBe(2);
        catalogue.Batches.Single().ShouldBe(["alpha quest", "beta quest"]);
        items.Select(i => i.ReadTitle).ShouldBe(["Alpha Quest", "Beta Quest"]);
        memo.Grounded.ShouldBe(2);
        memo.Reused.ShouldBe(2);
    }

    [Fact]
    public async Task Identical_reads_asked_by_calls_that_overlap_reach_the_provider_once()
    {
        var gate = new TaskCompletionSource();
        var catalogue = new RecordingCatalogue();
        var provider = new WaitingProvider(release: 1, gate.Task);
        var grounder = Grounder(catalogue, provider);
        var memo = new EvalGroundingMemo();

        var first = GroundAsync(grounder, memo, Reads("Alpha Quest", "Beta Quest"));
        await provider.FirstAsk.WaitAsync(Patience);
        var second = GroundAsync(grounder, memo, Reads("Beta Quest", "Alpha Quest"));
        gate.SetResult();

        var firstItems = await first.WaitAsync(Patience);
        var secondItems = await second.WaitAsync(Patience);

        provider.Asks.ShouldBe(2);
        catalogue.Batches.Count.ShouldBe(1);
        firstItems.Select(i => i.ReadTitle).ShouldBe(["Alpha Quest", "Beta Quest"]);
        secondItems.Select(i => i.ReadTitle).ShouldBe(["Beta Quest", "Alpha Quest"]);
        memo.Grounded.ShouldBe(2);
        memo.Reused.ShouldBe(2);
    }

    [Fact]
    public async Task A_read_whose_grounding_failed_is_asked_again_by_the_next_call()
    {
        var catalogue = new RecordingCatalogue { FailOnce = new InvalidOperationException("the catalogue is down") };
        var provider = new WaitingProvider(release: 1);
        var grounder = Grounder(catalogue, provider);
        var memo = new EvalGroundingMemo();

        await Should.ThrowAsync<InvalidOperationException>(() => GroundAsync(grounder, memo, Reads("Alpha Quest", "Beta Quest")));
        memo.Grounded.ShouldBe(0);

        var items = await GroundAsync(grounder, memo, Reads("Alpha Quest", "Beta Quest")).WaitAsync(Patience);

        items.Select(i => i.Verdict).ShouldAllBe(v => v == "matched");
        provider.Asks.ShouldBe(2);
        memo.Grounded.ShouldBe(2);
    }

    [Fact]
    public async Task Cancelling_stops_the_asks_in_flight_starts_no_more_and_keeps_nothing()
    {
        using var cancel = new CancellationTokenSource();
        var provider = new WaitingProvider(release: 3, new TaskCompletionSource().Task);
        var grounder = Grounder(new RecordingCatalogue(), provider, parallelism: 3);
        var memo = new EvalGroundingMemo();

        var running = GroundAsync(grounder, memo, Reads(Titles), cancel.Token);
        while (provider.MostAtOnce < 3)
        {
            await Task.Delay(5);
        }

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => running.WaitAsync(Patience));
        provider.Asks.ShouldBe(3);
        provider.Cancelled.ShouldBe(3);
        memo.Grounded.ShouldBe(0);

        var items = await GroundAsync(Grounder(new RecordingCatalogue(), new WaitingProvider(release: 1)), memo, Reads(Titles)).WaitAsync(Patience);
        items.Select(i => i.Verdict).ShouldAllBe(v => v == "matched");
        memo.Grounded.ShouldBe(Titles.Length);
    }

    [Fact]
    public async Task Every_ask_has_the_patient_budget_and_is_not_given_up_on_at_the_first_failure()
    {
        var provider = new WaitingProvider(release: 1);
        var grounder = Grounder(new RecordingCatalogue(), provider);

        await GroundAsync(grounder, new EvalGroundingMemo(), Reads(Titles)).WaitAsync(Patience);

        provider.AskedPatiently.ShouldBeTrue();
    }

    [Fact]
    public async Task A_read_that_runs_out_of_its_own_budget_is_pending_and_the_read_queued_behind_it_still_has_a_full_budget()
    {
        var grounder = Grounder(new RecordingCatalogue(), new SlowForSomeProvider(), parallelism: 1, budget: TimeSpan.FromMilliseconds(300));

        var items = await GroundAsync(grounder, new EvalGroundingMemo(), Reads("Slow Poke Quest", "Quick Quest")).WaitAsync(Patience);

        items.Select(i => i.Verdict).ShouldBe(["pending", "matched"]);
    }
}
