using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Evals;

// The eval grounds a shelf the way /identify does, and that includes the pass over the whole picture: a spine that is only a
// series' subtitle ("nEMESIS") is asked again with the series its neighbour carries. The answer depends on the neighbours, so
// the run's memo, which remembers a read on its own, never holds it. The reads are the misreads of a stored eval.
public class EvalGrounderSeriesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    private static string Key(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static ProviderRelease Release(string title, string id, int year) => new()
    {
        Provider = "scripted",
        ExternalId = id,
        Title = title,
        Kind = WorkKind.Film,
        Format = ReleaseFormat.Dvd,
        Year = year,
    };

    /// <summary>Knows the releases it was told for exactly one asked title (whatever the case and punctuation), and can hang on one.</summary>
    private sealed class Scripted : ICatalogProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ProviderRelease[]> _known = [];

        private readonly List<string> _titles = [];

        /// <summary>The titles asked about so far, in order.</summary>
        public List<string> Titles
        {
            get
            {
                lock (_gate)
                {
                    return [.. _titles];
                }
            }
        }

        public string? Hangs { get; set; }

        public string Name => "scripted";

        public bool Supports(WorkKind kind) => true;

        public Scripted Know(string asked, params ProviderRelease[] releases)
        {
            _known[Key(asked)] = releases;
            return this;
        }

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            lock (_gate)
            {
                _titles.Add(query.Title);
            }

            if (Hangs is { } hangs && Key(hangs) == Key(query.Title))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return _known.TryGetValue(Key(query.Title), out var found) ? found : [];
        }
    }

    private static Scripted StarTrek() => new Scripted()
        .Know("STAR TREK FIRST CONTACT", Release("Star Trek: First Contact", "f1", 1996))
        .Know("nEMESIS", Release("Nemesis", "n1", 1998), Release("Nemesis", "n2", 2005))
        .Know("Star Trek nEMESIS", Release("Star Trek: Nemesis", "f2", 2002));

    private static EvalReadItemInput Spine(string title, double? x) => new(
        new IdentifyItemInput(
            null, null, title, title, null, null, null, WorkKind.Film, null, null, null, null,
            Box: x is { } left ? new ReadBoxInput(left, 0.1, 0.05, 0.8) : null),
        null);

    /// <summary>A shelf: the two spines given, and two more far away (a picture of four reads or more is a shelf).</summary>
    private static EvalReadItemInput[] Shelf(params EvalReadItemInput[] spines) =>
        [.. spines, Spine("ALPHA QUEST", 0.70), Spine("BETA QUEST", 0.80)];

    private static EvalGrounder Grounder(Scripted providers)
    {
        var scanning = new ScanningOptions { JobResolveBudget = TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, new Catalogue(), [providers], scanning, NullLogger<TextSearchResolver>.Instance);
        return new EvalGrounder(
            new IdentifyGrounding(resolver, new FixedScoring()), null!, scanning,
            Options.Create(new EvalsOptions { GroundingParallelism = 4 }), new NoSerials(), TimeProvider.System);
    }

    private static Task<IReadOnlyList<GroundedItem>> GroundAsync(EvalGrounder grounder, EvalGroundingMemo memo, params EvalReadItemInput[] reads) =>
        grounder.GroundAsync(ScanEngine.VisionPipeline, reads, memo, printKind: null, CancellationToken.None).WaitAsync(Patience);

    [Fact]
    public async Task A_nemesis_beside_First_Contact_that_found_only_the_novels_is_offered_the_film()
    {
        var items = await GroundAsync(Grounder(StarTrek()), new EvalGroundingMemo(), Shelf(Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", 0.36)));

        items[0].Verdict.ShouldBe("matched");
        items[1].Read!.Profile.ShouldBe("shelf_spine");
        items[1].Verdict.ShouldBe("ambiguous");
        items[1].Candidates.Select(c => c.Title).ShouldBe(["Star Trek: Nemesis", "Nemesis", "Nemesis"]); // the film first, the novels it found after it
        items.Skip(2).Select(i => i.Verdict).ShouldAllBe(v => v == "not_found");
    }

    [Fact]
    public async Task The_run_never_remembers_the_series_answer_for_the_read_on_its_own()
    {
        var grounder = Grounder(StarTrek());
        var memo = new EvalGroundingMemo();

        var withNeighbour = await GroundAsync(grounder, memo, Shelf(Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", 0.36)));
        var alone = await GroundAsync(grounder, memo, Shelf(Spine("nEMESIS", 0.36), Spine("GAMMA QUEST", 0.45)));

        withNeighbour[1].Candidates.Select(c => c.Title).ShouldBe(["Star Trek: Nemesis", "Nemesis", "Nemesis"]);
        alone[0].Verdict.ShouldBe("ambiguous");
        alone[0].Candidates.Select(c => c.Title).ShouldBe(["Nemesis", "Nemesis"]); // the read as it stands alone, from the memo
        memo.Reused.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_lone_spine_asks_nothing_with_a_series()
    {
        var providers = StarTrek();

        await GroundAsync(Grounder(providers), new EvalGroundingMemo(), Shelf(Spine("nEMESIS", 0.36), Spine("GAMMA QUEST", 0.45)));

        providers.Titles.ShouldNotContain(title => title.Contains("Star Trek", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_read_with_no_box_never_asks()
    {
        var providers = StarTrek();

        var items = await GroundAsync(Grounder(providers), new EvalGroundingMemo(), Shelf(Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", null)));

        items[1].Candidates.Select(c => c.Title).ShouldBe(["Nemesis", "Nemesis"]);
        providers.Titles.ShouldNotContain(title => title.Equals("Star Trek nEMESIS", StringComparison.OrdinalIgnoreCase));
    }

    // The series round has its own fixed budget (SeriesNeighbour.Budget), the one /identify gives it, not the patient one the first
    // asks get: the eval measures what a person scanning could be given. A round that runs out leaves every answer as it was.

    [Fact]
    public async Task A_series_round_that_ran_out_of_time_leaves_a_read_that_found_nothing_unscored_after_the_live_budget_not_the_jobs()
    {
        var providers = new Scripted()
            .Know("STAR TREK FIRST CONTACT", Release("Star Trek: First Contact", "f1", 1996));
        providers.Hangs = "Star Trek nEMESIS";
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var items = await GroundAsync(Grounder(providers), new EvalGroundingMemo(), Shelf(Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", 0.36)));

        items.ShouldHaveSingleItem().Verdict.ShouldBe("matched"); // the other read is not_found, so skipped (a pending one would still show: nothing could finish it later)
        providers.Titles.ShouldContain("Star Trek nEMESIS");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10)); // the job budget of the grounder is 30 s; the series round took its own
    }

    [Fact]
    public async Task A_series_round_that_ran_out_of_time_leaves_an_ambiguous_read_with_its_options()
    {
        var providers = StarTrek();
        providers.Hangs = "Star Trek nEMESIS";

        var items = await GroundAsync(Grounder(providers), new EvalGroundingMemo(), Shelf(Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", 0.36)));

        items[1].Verdict.ShouldBe("ambiguous");
        items[1].Candidates.Select(c => c.Title).ShouldBe(["Nemesis", "Nemesis"]);
    }

    [Fact]
    public async Task A_series_ask_cut_short_by_cancelling_is_not_an_answer()
    {
        var providers = StarTrek();
        providers.Hangs = "Star Trek nEMESIS";
        using var cancel = new CancellationTokenSource();

        var grounding = Grounder(providers).GroundAsync(
            ScanEngine.VisionPipeline, Shelf(Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", 0.36)), new EvalGroundingMemo(), printKind: null, cancel.Token);
        var deadline = DateTime.UtcNow + Patience;
        while (!providers.Titles.Contains("Star Trek nEMESIS") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => grounding.WaitAsync(Patience));
    }
}
