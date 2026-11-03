using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Evals;

// The eval grounds a cover the way /identify does: one that found only the wrong release is asked once more without its possessive
// name, what that finds is put first and offered, and the answer the run remembers is that final one. The two must never
// disagree, or the eval would measure something the app does not do.
public class EvalGrounderPossessiveRetryTests
{
    private const string Misread = "WALT DISNEY'S MASTERPIECE PINOCCHIO";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    /// <summary>Two books a provider knows: a collection that shares words with the misread cover, and the book itself.</summary>
    private static Providers Known2() => new(false, Known("Walt Disney Masterpieces", "b1"), Known("Pinocchio", "b2"))
    {
        Finds = (title, book) => string.Equals(book.Title, title, StringComparison.OrdinalIgnoreCase)
            || (title.Contains("MASTERPIECE", StringComparison.OrdinalIgnoreCase) && book.Title.Contains("Masterpiece", StringComparison.OrdinalIgnoreCase)),
    };

    private static ScanningOptions Scanning() => new() { JobResolveBudget = TimeSpan.FromSeconds(30) };

    private static IdentifyGrounding Live(Catalogue catalogue, Providers providers) => new(
        new TextSearchResolver(null!, catalogue, [providers], Scanning(), NullLogger<TextSearchResolver>.Instance), new FixedScoring());

    private static EvalGrounder Grounder(Catalogue catalogue, Providers providers) => new(
        Live(catalogue, providers), null!, Scanning(), Options.Create(new EvalsOptions { GroundingParallelism = 4 }), new NoSerials(), TimeProvider.System);

    private static EvalReadItemInput Cover(string title) => new(
        new IdentifyItemInput(null, null, title, title, null, null, null, WorkKind.Book, null, null, null, null),
        null);

    [Fact]
    public async Task A_cover_that_found_only_the_wrong_release_gets_the_same_answer_from_the_eval_as_from_the_app()
    {
        var live = await Live(new Catalogue(), Known2()).GroundAsync(
            new ReadFacts(Misread, Misread, null, null, null, WorkKind.Book, null, null, null),
            ScoringProfileKind.Cover, TimeSpan.FromSeconds(30), CancellationToken.None);

        var items = await Grounder(new Catalogue(), Known2())
            .GroundAsync(ScanEngine.VisionPipeline, [Cover(Misread)], new EvalGroundingMemo(), printKind: null, CancellationToken.None)
            .WaitAsync(Patience);

        var item = items.Single();
        item.Read!.Profile.ShouldBe("cover");
        item.ReadTitle.ShouldBe(Misread);
        live.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        live.FoundByTrimmedSpan.ShouldBeTrue();
        live.Options.Select(o => o.Provider!.Title).ShouldBe(["Pinocchio", "Walt Disney Masterpieces"]);
        item.Verdict.ShouldBe("ambiguous");
        item.Candidates.Select(c => c.Title).ShouldBe(live.Options.Select(o => o.Provider!.Title));
        item.Candidates.Select(c => c.Score).ShouldBe(live.Options.Select(o => o.Score));
    }

    [Fact]
    public async Task A_read_whose_best_release_a_provider_gave_and_already_holds_the_name_is_not_asked_again_by_the_app_or_the_eval()
    {
        // Two releases of the real title tie (two years), and a shorter title exists that asking would put first.
        Providers Holding() => new(false, Known("Schindler's List", "b1"), Known("Schindler's List", "b2") with { Year = 1993 }, Known("List", "b3"))
        {
            Finds = (title, book) => title.Contains("SCHINDLER", StringComparison.OrdinalIgnoreCase)
                ? book.Title.Contains("Schindler", StringComparison.Ordinal)
                : string.Equals(book.Title, title, StringComparison.OrdinalIgnoreCase),
        };
        var liveProviders = Holding();
        var evalProviders = Holding();

        var live = await Live(new Catalogue(), liveProviders).GroundAsync(
            new ReadFacts("SCHINDLER'S LIST", "SCHINDLER'S LIST", null, null, null, WorkKind.Book, null, null, null),
            ScoringProfileKind.Cover, TimeSpan.FromSeconds(30), CancellationToken.None);
        var items = await Grounder(new Catalogue(), evalProviders)
            .GroundAsync(ScanEngine.VisionPipeline, [Cover("SCHINDLER'S LIST")], new EvalGroundingMemo(), printKind: null, CancellationToken.None)
            .WaitAsync(Patience);

        live.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        live.FoundByTrimmedSpan.ShouldBeFalse();
        live.Options.Select(o => o.Provider!.Title).ShouldNotContain("List");
        liveProviders.Titles.ShouldNotContain(title => string.Equals(title, "LIST", StringComparison.OrdinalIgnoreCase));
        evalProviders.Titles.ShouldNotContain(title => string.Equals(title, "LIST", StringComparison.OrdinalIgnoreCase));
        items.Single().Candidates.Select(c => c.Title).ShouldBe(live.Options.Select(o => o.Provider!.Title));
    }

    [Fact]
    public async Task The_answer_the_run_remembers_is_the_one_after_the_retry()
    {
        var catalogue = new Catalogue();
        var grounder = Grounder(catalogue, Known2());
        var memo = new EvalGroundingMemo();

        var first = await grounder.GroundAsync(ScanEngine.VisionPipeline, [Cover(Misread)], memo, printKind: null, CancellationToken.None).WaitAsync(Patience);
        var calls = catalogue.Batches.Count;
        var second = await grounder.GroundAsync(ScanEngine.VisionPipeline, [Cover(Misread)], memo, printKind: null, CancellationToken.None).WaitAsync(Patience);

        first.Single().Candidates.Select(c => c.Title).ShouldBe(["Pinocchio", "Walt Disney Masterpieces"]);
        second.Single().Candidates.Select(c => c.Title).ShouldBe(first.Single().Candidates.Select(c => c.Title));
        catalogue.Batches.Count.ShouldBe(calls); // the second photo asked nothing: the retried read was remembered
    }
}
