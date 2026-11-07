using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A shelf spine about to be skipped (nothing found after every retry, or an author's name that only found a book titled with it) is
// asked again with up to three other lines of its spine. What a line finds is kept only when a creator of the release it found is
// printed on the same spine; it is offered (never matched) with a "line" step. Names and titles are neutral.
public class IdentifyGroundingLineRetryTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static IdentifyGrounding Grounding(Catalogue catalogue) => new(
        new TextSearchResolver(null!, catalogue, [new Providers()], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    /// <summary>A kindless spine read: the title the phone chose, and every line of the spine's piece.</summary>
    private static ReadFacts Spine(string title, string[]? lines, string? creator = null, string? creatorAsRead = null) => new(
        title, string.Join(' ', lines ?? [title]), creator, null, null, null, null, null, null,
        CreatorAsRead: creatorAsRead, Lines: lines is null ? null : SpineLines.Join(lines));

    private static Task<BarcodeResolution> GroundAsync(
        IdentifyGrounding grounding, ReadFacts facts, ScoringProfileKind profile = ScoringProfileKind.ShelfSpine) =>
        grounding.GroundAsync(facts, profile, Budget, CancellationToken.None);

    [Fact]
    public async Task A_spine_the_catalogue_found_nothing_for_is_asked_again_with_another_line_and_kept_when_its_author_is_printed_there()
    {
        var real = BookBy("Copper Lantern", "Tess Harrowgate");
        var catalogue = new Catalogue(real);

        var answer = await GroundAsync(
            Grounding(catalogue), Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN", "TESS HARROWGATE"]));

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous); // offered, never matched
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Options[0].LocalReleaseId.ShouldBe(real.Release.Id);
        answer.Steps.ShouldContain(s => s.Provider == "line" && s.Outcome.StartsWith("COPPER LANTERN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_author_name_spine_about_to_be_skipped_is_asked_with_its_spines_title_line()
    {
        var real = BookBy("Copper Lantern", "Tess Harrowgate");
        var catalogue = new Catalogue(BookBy("Tess Harrowgate", "Tess Harrowgate"), BookBy("Tess Harrowgate", "Tess Harrowgate"), real);

        var answer = await GroundAsync(Grounding(catalogue), Spine("TESS HARROWGATE", ["TESS HARROWGATE", "COPPER LANTERN"]));

        catalogue.Lookups.ShouldBe(["tess harrowgate", "copper lantern"]);
        answer.Options[0].LocalReleaseId.ShouldBe(real.Release.Id);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Steps.ShouldContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_lines_find_whose_author_is_not_printed_on_the_spine_is_not_kept()
    {
        var catalogue = new Catalogue(BookBy("Copper Lantern", "Mara Quillon"));

        var answer = await GroundAsync(
            Grounding(catalogue), Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN", "TESS HARROWGATE"]));

        catalogue.Lookups.ShouldContain("copper lantern"); // it was asked
        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_lines_find_with_no_known_author_is_not_kept()
    {
        var catalogue = new Catalogue(Book("Copper Lantern"));

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN", "TESS HARROWGATE"]));

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }

    [Fact]
    public async Task A_line_that_is_the_authors_own_name_found_as_a_book_titled_with_it_is_not_kept()
    {
        var catalogue = new Catalogue(BookBy("Tess Harrowgate", "Tess Harrowgate"));

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", ["XQZV WRTK", "TESS HARROWGATE"]));

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }

    [Fact]
    public async Task Name_banner_and_publisher_lines_and_short_lines_are_never_asked()
    {
        var catalogue = new Catalogue();
        var lines = new[] { "XQZV WRTK", "TESS HARROWGATE", "BESTSELLING AUTHOR", "PENGUIN BOOKS", "WIDESCREEN VHS", "ABC", "1999" };

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", lines, creator: "Tess Harrowgate"));

        catalogue.Lookups.ShouldBe(["xqzv wrtk"]);
        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }

    [Fact]
    public async Task The_title_and_its_trimmed_spans_are_never_asked_as_lines()
    {
        var catalogue = new Catalogue();

        await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK PLMN", ["XQZV WRTK PLMN", "WRTK PLMN", "XQZV WRTK"]));

        catalogue.Lookups.ShouldBe(["xqzv wrtk plmn", "wrtk plmn", "xqzv wrtk"]); // the whole read and its two spans, once each
    }

    [Fact]
    public async Task A_spine_with_no_other_lines_is_skipped_as_before()
    {
        var catalogue = new Catalogue(BookBy("Copper Lantern", "Tess Harrowgate"));

        var withoutLines = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", null));
        var onlyTitle = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", ["XQZV WRTK"]));

        withoutLines.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        onlyTitle.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldAllBe(l => l == "xqzv wrtk");
    }

    [Fact]
    public async Task Other_lines_are_asked_longest_first_and_at_most_three()
    {
        var catalogue = new Catalogue();
        var lines = new[] { "XQZV WRTK", "BRAVO ECHO", "ALPHA", "CHARLIE DELTA", "FOXTROT GOLF HOTEL", "INDIA JULIET" };

        await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", lines));

        catalogue.Lookups.ShouldBe(["xqzv wrtk", "foxtrot golf hotel", "charlie delta", "india juliet"]);
    }

    [Fact]
    public async Task A_matched_spine_is_never_asked_by_line()
    {
        var catalogue = new Catalogue(BookBy("Copper Lantern", "Tess Harrowgate"));

        var matching = Spine("COPPER LANTERN", ["COPPER LANTERN", "TESS HARROWGATE", "ANOTHER LINE HERE"], creator: "Tess Harrowgate") with { Kind = WorkKind.Book };

        var answer = await GroundAsync(Grounding(catalogue), matching);

        answer.Verdict.ShouldBe(ResolutionVerdict.Matched);
        catalogue.Lookups.ShouldBe(["copper lantern"]);
    }

    [Fact]
    public async Task An_ambiguous_spine_that_is_not_an_authors_name_is_never_asked_by_line()
    {
        var catalogue = new Catalogue(BookBy("Copper Lantern", "Tess Harrowgate"), BookBy("Copper Lantern", "Mara Quillon"));

        var answer = await GroundAsync(Grounding(catalogue), Spine("COPPER LANTERN", ["COPPER LANTERN", "ANOTHER LINE HERE"]));

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        catalogue.Lookups.ShouldBe(["copper lantern"]);
    }

    [Fact]
    public async Task A_cover_read_is_never_asked_by_line()
    {
        var catalogue = new Catalogue(BookBy("Copper Lantern", "Tess Harrowgate"));

        var answer = await GroundAsync(
            Grounding(catalogue), Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN", "TESS HARROWGATE"]), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldBe(["xqzv wrtk"]);
    }

    [Fact]
    public void Two_reads_with_the_same_lines_are_equal_and_with_other_lines_are_not()
    {
        var a = Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN"]);
        var b = Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN"]);
        var c = Spine("XQZV WRTK", ["XQZV WRTK", "QUIET HARBOR"]);

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(c);
    }

    [Fact]
    public async Task A_name_line_the_phone_did_not_send_as_the_creator_is_not_its_own_authors_witness()
    {
        // No creator was sent, so the name line is asked; it finds a book by that name, and the only print of the name is the line itself.
        var catalogue = new Catalogue(BookBy("Tess Harrowgate Stories", "Tess Harrowgate"))
        {
            Finds = (book, search) => book.Work.NormalizedTitle.StartsWith(search.NormalizedTitle, StringComparison.Ordinal),
        };

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", ["XQZV WRTK", "TESS HARROWGATE"]));

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    // ---- an author's name read: the creator swap, then the lines -----------------------------------------------------------

    /// <summary>An author-name spine: the title read is the author, the creator as read is another book's title, and the spine prints a third line.</summary>
    private static ReadFacts NameSpine() =>
        Spine("TESS HARROWGATE", ["TESS HARROWGATE", "COPPER LANTERN"], creator: "Quiet Harbor", creatorAsRead: "Quiet Harbor");

    private static Catalogue NameCatalogue(params LocalHit[] more) =>
        new([BookBy("Tess Harrowgate", "Tess Harrowgate"), BookBy("Tess Harrowgate", "Tess Harrowgate"), .. more]);

    [Fact]
    public async Task An_author_name_spine_whose_creator_swap_finds_a_weaker_release_is_still_asked_with_its_spines_title_line()
    {
        var real = BookBy("Copper Lantern", "Tess Harrowgate");
        var catalogue = NameCatalogue(BookBy("Quiet Harbor", "Mara Quillon"), real);

        var answer = await GroundAsync(Grounding(catalogue), NameSpine());

        catalogue.Lookups.ShouldBe(["tess harrowgate", "quiet harbor", "copper lantern"]);
        answer.Options[0].LocalReleaseId.ShouldBe(real.Release.Id);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Steps.ShouldContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_swap_that_finds_a_release_scoring_above_the_name_reads_own_still_ends_the_retries()
    {
        var swapped = BookBy("Quiet Harbor", "Tess Harrowgate");
        var catalogue = NameCatalogue(swapped, BookBy("Copper Lantern", "Tess Harrowgate"));

        var answer = await GroundAsync(Grounding(catalogue), NameSpine());

        catalogue.Lookups.ShouldBe(["tess harrowgate", "quiet harbor"]);
        answer.Options[0].LocalReleaseId.ShouldBe(swapped.Release.Id);
        answer.Steps.ShouldContain(s => s.Provider == "swapped");
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_name_spine_whose_swap_and_lines_find_nothing_keeps_the_answer_it_had()
    {
        var catalogue = NameCatalogue(BookBy("Quiet Harbor", "Mara Quillon"));

        var answer = await GroundAsync(Grounding(catalogue), NameSpine());

        catalogue.Lookups.ShouldBe(["tess harrowgate", "quiet harbor", "copper lantern"]);
        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options[0].Provider.ShouldBeNull();
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    // ---- a line is judged by the author its spine prints -----------------------------------------------------------------------

    private static readonly string[] CopperLines = ["XQZV WRTK", "COPPER LANTERN", "TESS HARROWGATE"];

    [Fact]
    public async Task A_spines_line_is_judged_by_the_release_whose_author_the_spine_prints_not_by_a_same_titled_release_with_no_author()
    {
        var other = BookBy("Copper Lantern", "Mara Quillon");
        var real = BookBy("Copper Lantern", "Tess Harrowgate");
        var catalogue = new Catalogue(Film("Copper Lantern"), other, real);

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", CopperLines, creator: "Iron Fox"));

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Options[0].LocalReleaseId.ShouldBe(real.Release.Id);
        answer.Steps.ShouldContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_printed_author_does_not_lift_a_release_whose_title_is_not_the_lines()
    {
        // The line finds a creatorless film and the printed author's other book; scored with that author the other book gains on the film,
        // but its title is not the line's.
        var catalogue = new Catalogue(Film("Copper Lantern Tales"), BookBy("Copper Harbor Lantern Light", "Tess Harrowgate"))
        {
            Finds = (_, search) => search.NormalizedTitle == "copper lantern",
        };

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", CopperLines, creator: "Iron Fox"));

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    [Fact]
    public void The_creator_a_spine_prints_is_the_first_found_one_whose_every_word_is_on_its_other_lines_and_is_not_the_line_itself()
    {
        var facts = Spine("XQZV WRTK", CopperLines);
        (string? Title, IReadOnlyList<string> Creators)[] found =
        [
            ("Copper Lantern", []),
            ("Copper Lantern", ["Mara Quillon"]),
            ("Tess Harrowgate", ["Tess Harrowgate"]),
            ("Copper Lantern", ["Tess Harrowgate"]),
        ];

        SpineLines.PrintedCreator(facts, "COPPER LANTERN", found).ShouldBe("Tess Harrowgate");
        SpineLines.PrintedCreator(facts, "TESS HARROWGATE", found).ShouldBeNull("the line is the only print of that name");
        SpineLines.PrintedCreator(facts, "COPPER LANTERN", found[..2]).ShouldBeNull();
    }

    // ---- the line asks share the read's budget: the Pending rules of the spans ------------------------------------------------

    private static readonly string[] BudgetLines = ["XQZV WRTK", "FOXTROT GOLF HOTEL", "CHARLIE DELTA", "INDIA JULIET"];

    private static async Task<(BarcodeResolution Retried, Catalogue Catalogue)> RetryAsync(
        Func<CancellationTokenSource, Task<ProviderAnswers>> ask, bool budgetSpentAlready)
    {
        var catalogue = new Catalogue();
        var grounding = Grounding(catalogue);
        var facts = Spine("XQZV WRTK", BudgetLines);
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        using var budget = new CancellationTokenSource();
        if (budgetSpentAlready)
        {
            await budget.CancelAsync();
        }

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, _ => ask(budget), budget.Token)], CancellationToken.None);
        return (answers.Single(), catalogue);
    }

    [Fact]
    public async Task A_spine_whose_budget_was_spent_before_any_line_was_asked_goes_pending()
    {
        var (retried, catalogue) = await RetryAsync(_ => Task.FromResult(ProviderAnswers.None), budgetSpentAlready: true);

        retried.Verdict.ShouldBe(ResolutionVerdict.Pending);
        retried.Steps.ShouldContain(new TraceStep("trimmed", "cut_short", 0));
        catalogue.Lookups.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_budget_that_runs_out_after_the_first_line_leaves_the_other_lines_unasked_and_the_spine_pending()
    {
        var (retried, catalogue) = await RetryAsync(async budget =>
        {
            await budget.CancelAsync(); // the first line's ask finished, but the time is gone before the next can be asked
            return ProviderAnswers.None;
        }, budgetSpentAlready: false);

        retried.Verdict.ShouldBe(ResolutionVerdict.Pending);
        catalogue.Lookups.ShouldBe(["foxtrot golf hotel"]);
    }
}
