using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Evals;

// The eval grounds a spine that found nothing the way /identify does: asked again without the stray words at its ends, the
// release a span finds offered (never matched), and the answer remembered for the run is that final one. A shelf is four reads
// or more, so every photo here has four. Titles are neutral.
public class EvalGrounderTrimmedRetryTests
{
    // One stray word at each end: the providers' own ladder drops words from one end only, so it never reaches the title.
    private const string Stray = "GARY QUIET HARBOR NOVEL";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    private static EvalReadItemInput Book(string title) => new(
        new IdentifyItemInput(null, null, title, title, null, null, null, WorkKind.Book, null, null, null, null),
        null);

    /// <summary>Three short reads, which are not retried (two words), and the stray one.</summary>
    private static EvalReadItemInput[] Shelf(string stray = Stray) => [Book("Alpha Quest"), Book("Beta Quest"), Book(stray), Book("Gamma Quest")];

    private static EvalGrounder Grounder(Catalogue catalogue, Providers providers, int parallelism = 4)
    {
        var scanning = new ScanningOptions { JobResolveBudget = TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, catalogue, [providers], scanning, NullLogger<TextSearchResolver>.Instance);
        return new EvalGrounder(
            new IdentifyGrounding(resolver, new FixedScoring()), null!, scanning,
            Options.Create(new EvalsOptions { GroundingParallelism = parallelism }), new NoSerials(), TimeProvider.System);
    }

    private static Task<IReadOnlyList<GroundedItem>> GroundAsync(EvalGrounder grounder, EvalGroundingMemo memo, params EvalReadItemInput[] reads) =>
        grounder.GroundAsync(ScanEngine.VisionPipeline, reads, memo, printKind: null, CancellationToken.None).WaitAsync(Patience);

    [Fact]
    public async Task A_spine_that_found_nothing_is_offered_the_release_a_trimmed_span_found()
    {
        var catalogue = new Catalogue();
        var grounder = Grounder(catalogue, new Providers(false, Known("Quiet Harbor")));

        var items = await GroundAsync(grounder, new EvalGroundingMemo(), Shelf());

        catalogue.Batches.Count.ShouldBeGreaterThan(1); // the whole read found nothing; a span was looked for
        items[2].ReadTitle.ShouldBe(Stray);
        items[2].Verdict.ShouldBe("ambiguous");
        items[2].Candidates.Select(c => c.Title).ShouldBe(["Quiet Harbor"]);
        items[2].Read!.Profile.ShouldBe("shelf_spine");
        items.Where((_, i) => i != 2).Select(i => i.Verdict).ShouldAllBe(v => v == "not_found");
    }

    [Fact]
    public async Task The_same_read_on_its_own_is_scored_as_a_cover_and_finds_nothing_so_the_retry_found_it()
    {
        var grounder = Grounder(new Catalogue(), new Providers(false, Known("Quiet Harbor")));

        var items = await GroundAsync(grounder, new EvalGroundingMemo(), Book(Stray));

        items.Single().Read!.Profile.ShouldBe("cover");
        items.Single().Verdict.ShouldBe("not_found");
    }

    [Fact]
    public async Task A_read_a_provider_could_not_answer_stays_pending_and_is_not_retried()
    {
        var catalogue = new Catalogue();
        var grounder = Grounder(catalogue, new Providers(down: true));

        var items = await GroundAsync(grounder, new EvalGroundingMemo(), Shelf());

        items[2].Verdict.ShouldBe("pending");
        catalogue.Batches.Count.ShouldBe(1); // the photo's one catalogue call; no span was looked for
    }

    [Fact]
    public async Task A_read_none_of_whose_spans_finds_a_release_stays_not_found()
    {
        var catalogue = new Catalogue();
        var grounder = Grounder(catalogue, new Providers());

        var items = await GroundAsync(grounder, new EvalGroundingMemo(), Shelf());

        items[2].Verdict.ShouldBe("not_found");
        items[2].Candidates.ShouldBeEmpty();
        catalogue.Lookups.Count.ShouldBe(4 + TrimmedSpans.MaxSpans); // the photo's four reads, then at most four spans of the one retried
    }

    [Fact]
    public async Task The_catalogue_calls_of_retries_that_run_together_are_kept_apart_from_each_other()
    {
        var catalogue = new Catalogue();
        var grounder = Grounder(catalogue, new Providers(), parallelism: 4);
        string[] strays = ["ONE TWO THREE FOUR", "FIVE SIX SEVEN EIGHT", "NINE TEN ELEVEN TWELVE", "THIRTEEN FOURTEEN FIFTEEN SIXTEEN"];

        await GroundAsync(grounder, new EvalGroundingMemo(), [.. strays.Select(Book)]);

        catalogue.Lookups.Count.ShouldBe(4 + 4 * TrimmedSpans.MaxSpans);
        catalogue.MostAtOnce.ShouldBe(1);
    }

    [Fact]
    public async Task The_answer_the_run_remembers_is_the_one_after_the_retry()
    {
        var catalogue = new Catalogue();
        var grounder = Grounder(catalogue, new Providers(false, Known("Quiet Harbor")));
        var memo = new EvalGroundingMemo();

        var first = await GroundAsync(grounder, memo, Shelf());
        var calls = catalogue.Batches.Count;
        var second = await GroundAsync(grounder, memo, Shelf());

        second.Select(i => i.Verdict).ShouldBe(first.Select(i => i.Verdict));
        second[2].Verdict.ShouldBe("ambiguous");
        catalogue.Batches.Count.ShouldBe(calls); // the second photo asked nothing: every read, the retried one too, was remembered
        memo.Reused.ShouldBe(4);
    }

    [Fact]
    public async Task A_retry_cut_short_by_cancelling_remembers_nothing()
    {
        using var cancel = new CancellationTokenSource();
        var catalogue = new Catalogue();
        var memo = new EvalGroundingMemo();
        var grounder = Grounder(catalogue, new Providers(false, Known("Quiet Harbor")));

        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(
            () => grounder.GroundAsync(ScanEngine.VisionPipeline, Shelf(), memo, printKind: null, cancel.Token));

        memo.Grounded.ShouldBe(0);
    }
}
