using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// An author-name read whose best release is titled with that name (the name is its own creator) is asked once more with its
// creator as the title before it is given up on, the way a read that found nothing is. What the swap finds is offered; a swap that
// finds nothing leaves the answer as it was, for FragmentReads to skip. Names are neutral.
public class IdentifyGroundingFragmentSwapTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static IdentifyGrounding Grounding(Catalogue catalogue) => new(
        new TextSearchResolver(null!, catalogue, [new Providers()], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    /// <summary>A kindless spine whose title the phone read as an author line and whose creator as the real title.</summary>
    private static ReadFacts NameRead(string title, string creatorAsRead) => new(
        title, title, null, null, null, null, null, null, null, CreatorAsRead: creatorAsRead);

    private static Task<BarcodeResolution> GroundAsync(IdentifyGrounding grounding, ReadFacts facts) =>
        grounding.GroundAsync(facts, ScoringProfileKind.ShelfSpine, Budget, CancellationToken.None);

    [Fact]
    public async Task An_author_name_read_whose_best_release_is_titled_with_that_name_asks_its_creator_as_the_title()
    {
        var real = BookBy("Copper Lantern", "Tess Harrowgate");
        var catalogue = new Catalogue(BookBy("Tess Harrowgate", "Tess Harrowgate"), BookBy("Tess Harrowgate", "Tess Harrowgate"), real);

        var answer = await GroundAsync(Grounding(catalogue), NameRead("TESS HARROWGATE", "COPPER LANTERN"));

        catalogue.Lookups.ShouldBe(["tess harrowgate", "copper lantern"]);
        answer.Options[0].LocalReleaseId.ShouldBe(real.Release.Id);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Steps.ShouldContain(s => s.Provider == "swapped");
    }

    [Fact]
    public async Task A_swap_that_finds_nothing_leaves_the_answer_as_it_was()
    {
        var first = BookBy("Tess Harrowgate", "Tess Harrowgate");
        var catalogue = new Catalogue(first, BookBy("Tess Harrowgate", "Tess Harrowgate"));

        var answer = await GroundAsync(Grounding(catalogue), NameRead("TESS HARROWGATE", "COPPER LANTERN"));

        catalogue.Lookups.ShouldBe(["tess harrowgate", "copper lantern"]);
        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.FoundByTrimmedSpan.ShouldBeFalse();
        answer.Steps.ShouldNotContain(s => s.Provider == "swapped");
    }

    [Fact]
    public async Task An_author_name_read_whose_answer_is_a_different_title_is_not_swapped()
    {
        var catalogue = new Catalogue(
            Book("Tess Harrowgate Story"), Book("Tess Harrowgate Story"), BookBy("Copper Lantern", "Tess Harrowgate"))
        {
            Finds = (book, search) => book.Work.NormalizedTitle.StartsWith(search.NormalizedTitle, StringComparison.Ordinal),
        };

        var answer = await GroundAsync(Grounding(catalogue), NameRead("TESS HARROWGATE", "COPPER LANTERN"));

        catalogue.Lookups.ShouldBe(["tess harrowgate"]);
        answer.Steps.ShouldNotContain(s => s.Provider == "swapped");
    }

    [Fact]
    public async Task A_title_that_is_no_creator_of_its_best_release_is_not_swapped()
    {
        var catalogue = new Catalogue(Book("Copper Lantern"), Book("Copper Lantern"), BookBy("Quiet Harbor", "Tess Harrowgate"));

        var answer = await GroundAsync(Grounding(catalogue), NameRead("COPPER LANTERN", "QUIET HARBOR"));

        catalogue.Lookups.ShouldBe(["copper lantern"]);
        answer.Steps.ShouldNotContain(s => s.Provider == "swapped");
    }
}
