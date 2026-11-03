using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A cover or spine that found nothing is asked again without its sequel number: the model read the game "Wing Commander IV: The
// Price of Freedom" as "WING COMMANDER II ...", a title that does not exist, and the wrong number blocked the whole search. The
// release found that way is offered for the person to pick, never matched. A cover is asked only this; a spine asks it after its
// trimmed spans and before its creator swap.
public class IdentifyGroundingSequelNumberRetryTests
{
    private const string Misread = "WING COMMANDER II THE PRICE OF FREEDOM";

    private const string WithoutNumber = "WING COMMANDER THE PRICE OF FREEDOM";

    private const string Real = "Wing Commander IV: The Price of Freedom";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static ReadFacts GameRead(string title = Misread) => new(
        title, title, null, null, null, WorkKind.Game, null, null, "PlayStation");

    private static ReadFacts BookRead(string title, string? creatorAsRead = null) => new(
        title, title, null, null, null, WorkKind.Book, null, null, null, CreatorAsRead: creatorAsRead);

    /// <summary>A title's key without its numbers: "wing commander 4 the price of freedom" is "wing commander the price of freedom".</summary>
    private static string Bare(string key) => string.Join(' ', key.Split(' ').Where(word => !word.All(char.IsAsciiDigit)));

    /// <summary>A catalogue that finds a release by its exact title, or by its title without the numbers in it (as the trigram search finds it by the rest).</summary>
    private static Catalogue ByTitleOrWithoutNumber(params LocalHit[] hits) => new(hits)
    {
        Finds = (hit, search) => hit.Work.NormalizedTitle == search.NormalizedTitle || Bare(hit.Work.NormalizedTitle) == search.NormalizedTitle,
    };

    private static IdentifyGrounding Grounding(Catalogue catalogue, Providers? providers = null) => new(
        new TextSearchResolver(null!, catalogue, [providers ?? new Providers()], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    private static Task<BarcodeResolution> GroundAsync(IdentifyGrounding grounding, ReadFacts facts, ScoringProfileKind profile) =>
        grounding.GroundAsync(facts, profile, Budget, CancellationToken.None);

    // ---- a cover ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cover_read_with_the_wrong_sequel_number_is_offered_the_release_found_without_it()
    {
        var game = Game(Real);
        var catalogue = ByTitleOrWithoutNumber(game);

        var answer = await GroundAsync(Grounding(catalogue), GameRead(), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options.Single().LocalReleaseId.ShouldBe(game.Release.Id);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        catalogue.Lookups.ShouldBe(["wing commander 2 the price of freedom", "wing commander the price of freedom"]);
    }

    [Fact]
    public async Task The_trace_says_the_number_was_dropped_after_the_lookup_of_the_whole_read()
    {
        var answer = await GroundAsync(Grounding(ByTitleOrWithoutNumber(Game(Real))), GameRead(), ScoringProfileKind.Cover);

        answer.Steps.ShouldContain(new TraceStep("numeral", $"{WithoutNumber} (dropped: II)", 0));
        var steps = answer.Steps.Select(s => s.Provider).ToList();
        steps.IndexOf("catalogue").ShouldBeLessThan(steps.IndexOf("numeral"));
        steps.Last().ShouldBe("profile");
    }

    [Fact]
    public async Task What_the_numberless_title_found_would_have_matched_but_is_only_offered()
    {
        var answer = await GroundAsync(Grounding(ByTitleOrWithoutNumber(Game(Real))), GameRead(), ScoringProfileKind.Cover);

        answer.Options.Single().Score.ShouldBeGreaterThanOrEqualTo(new ScoringThresholds().AmbiguousScore);
        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
    }

    [Fact]
    public async Task The_same_cover_read_finds_nothing_without_the_retry_in_the_same_catalogue()
    {
        // Control: the title as read is nothing in the catalogue, so it is the retry that found the game.
        var catalogue = ByTitleOrWithoutNumber(Game(Real));

        var answer = await GroundAsync(Grounding(catalogue), GameRead(), ScoringProfileKind.BareLabel);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldBe(["wing commander 2 the price of freedom"]);
    }

    [Fact]
    public async Task A_cover_read_asks_nothing_but_the_numberless_title_no_trimmed_span_and_no_swap()
    {
        var catalogue = new Catalogue();

        var answer = await GroundAsync(Grounding(catalogue), GameRead() with { CreatorAsRead = "SOMEONE ELSE" }, ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Options.ShouldBeEmpty();
        answer.Steps.ShouldNotContain(s => s.Provider == "numeral" || s.Provider == "trimmed" || s.Provider == "swapped");
        catalogue.Lookups.ShouldBe(["wing commander 2 the price of freedom", "wing commander the price of freedom"]);
    }

    [Fact]
    public async Task A_numberless_title_is_asked_with_the_reads_other_facts_and_what_was_read_stays_the_original_text()
    {
        var grounding = Grounding(new Catalogue());
        var facts = GameRead() with { Format = ReleaseFormat.GameDisc, Year = 1996, KindContext = WorkKind.Game };
        var notFound = grounding.Score(facts, ScoringProfileKind.Cover, LocalPool.Empty, ProviderAnswers.None);
        var asked = new List<ReadFacts>();

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.Cover, notFound, f => { asked.Add(f); return Task.FromResult(ProviderAnswers.None); }, CancellationToken.None)],
            CancellationToken.None);

        var numberless = asked.Single();
        numberless.Title.ShouldBe(WithoutNumber);
        numberless.Text.ShouldBe(Misread);
        numberless.Kind.ShouldBe(WorkKind.Game);
        numberless.Platform.ShouldBe("PlayStation");
        numberless.Format.ShouldBe(ReleaseFormat.GameDisc);
        numberless.Year.ShouldBe(1996);
        numberless.KindContext.ShouldBe(WorkKind.Game);
        facts.Title.ShouldBe(Misread);
        answers.Single().ShouldBeSameAs(notFound); // and nothing found: the answer is the one it had
    }

    // ---- a spine ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_spine_read_with_a_sequel_number_is_offered_the_release_found_without_it_after_its_four_spans()
    {
        var book = Book("Quiet Harbor 2: The Long Way");
        var catalogue = ByTitleOrWithoutNumber(book);

        var answer = await GroundAsync(Grounding(catalogue), BookRead("QUIET HARBOR 3 THE LONG WAY"), ScoringProfileKind.ShelfSpine);

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options.Single().LocalReleaseId.ShouldBe(book.Release.Id);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Steps.ShouldContain(new TraceStep("numeral", "QUIET HARBOR THE LONG WAY (dropped: 3)", 0));
        catalogue.Lookups.ShouldBe(
        [
            "quiet harbor 3 the long way", "harbor 3 the long way", "quiet harbor 3 the long", "harbor 3 the long", "3 the long way",
            "quiet harbor the long way",
        ]);
    }

    [Fact]
    public async Task A_spine_whose_trimmed_span_finds_a_release_never_asks_the_numberless_title()
    {
        var book = Book("Quiet Harbor 3");
        var catalogue = ByTitleOrWithoutNumber(book);

        var answer = await GroundAsync(Grounding(catalogue), BookRead("GARY QUIET HARBOR 3 NOVEL"), ScoringProfileKind.ShelfSpine);

        answer.Options.Single().LocalReleaseId.ShouldBe(book.Release.Id);
        answer.Steps.ShouldContain(s => s.Provider == "trimmed");
        answer.Steps.ShouldNotContain(s => s.Provider == "numeral");
        catalogue.Lookups.ShouldNotContain("gary quiet harbor novel");
    }

    [Fact]
    public async Task A_spine_asks_the_numberless_title_before_it_swaps_its_creator_in()
    {
        var lantern = BookBy("Copper Lantern", "Tess Harrowgate");
        var catalogue = ByTitleOrWithoutNumber(Book("Quiet Harbor 2: The Long Way"), lantern);

        var answer = await GroundAsync(Grounding(catalogue), BookRead("QUIET HARBOR 3 THE LONG WAY", "COPPER LANTERN"), ScoringProfileKind.ShelfSpine);

        answer.Steps.ShouldContain(s => s.Provider == "numeral");
        answer.Steps.ShouldNotContain(s => s.Provider == "swapped");
        answer.Options.ShouldNotContain(o => o.LocalReleaseId == lantern.Release.Id);
        catalogue.Lookups.ShouldNotContain("copper lantern");
    }

    [Fact]
    public async Task A_spine_whose_numberless_title_finds_nothing_swaps_its_creator_in_last()
    {
        var lantern = BookBy("Copper Lantern", "Tess Harrowgate");
        var catalogue = ByTitleOrWithoutNumber(lantern);

        var answer = await GroundAsync(Grounding(catalogue), BookRead("QUIET HARBOR 3 THE LONG WAY", "COPPER LANTERN"), ScoringProfileKind.ShelfSpine);

        answer.Options.Single().LocalReleaseId.ShouldBe(lantern.Release.Id);
        answer.Steps.ShouldContain(s => s.Provider == "swapped");
        answer.Steps.ShouldNotContain(s => s.Provider == "numeral");
        catalogue.Lookups[^2..].ShouldBe(["quiet harbor the long way", "copper lantern"]);
    }

    [Fact]
    public async Task A_numberless_title_the_spans_already_asked_is_not_asked_a_second_time()
    {
        var grounding = Grounding(new Catalogue());
        var facts = BookRead("GARY QUIET HARBOR 2");
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        var asked = new List<string?>();

        await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, f => { asked.Add(f.Title); return Task.FromResult(ProviderAnswers.None); }, CancellationToken.None)],
            CancellationToken.None);

        asked.ShouldBe(["QUIET HARBOR 2", "GARY QUIET HARBOR", "QUIET HARBOR", "HARBOR 2"]); // "GARY QUIET HARBOR" is the numberless title, and was a span
    }

    // ---- reads that are never asked again without a number --------------------------------------------------------------------------

    [Theory]
    [InlineData("I, ROBOT")]
    [InlineData("1984")]
    [InlineData("BLADE RUNNER 2049")]
    [InlineData("CATCH-22")]
    [InlineData("HARRY POTTER 7 8")]
    [InlineData("ROCKY II")]
    [InlineData("QUIET HARBOR 21 NIGHTS")]
    public async Task A_cover_read_with_no_sequel_number_in_it_is_not_asked_again(string title)
    {
        var catalogue = new Catalogue();

        var answer = await GroundAsync(Grounding(catalogue), GameRead(title), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.Count.ShouldBe(1);
        answer.Steps.ShouldNotContain(s => s.Provider == "numeral");
    }

    [Theory]
    [InlineData(ScoringProfileKind.BareLabel)]
    [InlineData(ScoringProfileKind.LooseCartridge)]
    [InlineData(ScoringProfileKind.BarcodeProduct)]
    public async Task A_read_scored_with_any_profile_but_the_cover_and_spine_ones_does_not_retry(ScoringProfileKind profile)
    {
        var catalogue = ByTitleOrWithoutNumber(Game(Real));

        var answer = await GroundAsync(Grounding(catalogue), GameRead(), profile);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldBe(["wing commander 2 the price of freedom"]);
        answer.Steps.ShouldNotContain(s => s.Provider == "numeral");
    }

    [Fact]
    public async Task A_cover_read_whose_title_a_product_code_decided_is_never_asked_again()
    {
        var catalogue = ByTitleOrWithoutNumber(Game(Real));

        var answer = await GroundAsync(Grounding(catalogue), GameRead() with { ProductCode = "SLUS-00001" }, ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldBe(["wing commander 2 the price of freedom"]);
    }

    [Fact]
    public async Task A_cover_read_that_is_ambiguous_never_retries()
    {
        var catalogue = new Catalogue(Game("Wing Commander II The Price of Freedom"), Game("Wing Commander II The Price of Freedom")); // two releases tie

        var answer = await GroundAsync(Grounding(catalogue), GameRead(), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.FoundByTrimmedSpan.ShouldBeFalse();
        catalogue.Lookups.ShouldBe(["wing commander 2 the price of freedom"]);
        answer.Steps.ShouldNotContain(s => s.Provider == "numeral");
    }

    [Fact]
    public async Task A_cover_read_that_already_matched_never_retries()
    {
        var catalogue = new Catalogue(Game("Wing Commander II The Price of Freedom"));

        var answer = await GroundAsync(Grounding(catalogue), GameRead(), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.Matched);
        answer.FoundByTrimmedSpan.ShouldBeFalse();
        catalogue.Lookups.ShouldBe(["wing commander 2 the price of freedom"]);
    }

    [Fact]
    public async Task A_cover_read_a_provider_could_not_answer_is_pending_and_does_not_retry()
    {
        var catalogue = new Catalogue(Book("Quiet Harbor 2: The Long Way"));

        var answer = await GroundAsync(Grounding(catalogue, new Providers(down: true)), BookRead("QUIET HARBOR 3 THE LONG WAY"), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.Pending);
        catalogue.Lookups.ShouldBe(["quiet harbor 3 the long way"]);
    }

    // ---- the budget: a retry the time ran out on is not an answer ------------------------------------------------------------------

    private static async Task<(BarcodeResolution Original, BarcodeResolution Retried, Catalogue Catalogue)> RetryCoverAsync(
        Func<CancellationTokenSource, Task<ProviderAnswers>> ask, bool budgetSpentAlready = false)
    {
        var catalogue = new Catalogue(); // nothing in it: what the retry would find is not the point
        var grounding = Grounding(catalogue);
        var facts = GameRead();
        var notFound = grounding.Score(facts, ScoringProfileKind.Cover, LocalPool.Empty, ProviderAnswers.None);
        using var budget = new CancellationTokenSource();
        if (budgetSpentAlready)
        {
            await budget.CancelAsync();
        }

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.Cover, notFound, _ => ask(budget), budget.Token)],
            CancellationToken.None);
        return (notFound, answers.Single(), catalogue);
    }

    [Fact]
    public async Task A_cover_read_whose_budget_was_spent_before_the_retry_was_tried_goes_pending()
    {
        var (original, retried, catalogue) = await RetryCoverAsync(_ => Task.FromResult(ProviderAnswers.None), budgetSpentAlready: true);

        retried.Verdict.ShouldBe(ResolutionVerdict.Pending);
        retried.Options.ShouldBeEmpty();
        retried.Steps.ShouldBe([.. original.Steps, new TraceStep("trimmed", "cut_short", 0)]);
        catalogue.Lookups.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_numberless_title_the_providers_could_not_answer_leaves_the_cover_read_pending_so_that_a_later_run_asks_again()
    {
        var (_, retried, _) = await RetryCoverAsync(_ => Task.FromResult(new ProviderAnswers([], [new TraceStep("igdb", "failed", 0)], true)));

        retried.Verdict.ShouldBe(ResolutionVerdict.Pending);
        retried.Steps.ShouldContain(new TraceStep("trimmed", "cut_short", 0));
    }

    [Fact]
    public async Task A_numberless_title_whose_provider_call_the_budget_cancelled_leaves_the_cover_read_pending()
    {
        var (_, retried, _) = await RetryCoverAsync(async budget =>
        {
            await budget.CancelAsync();
            return new ProviderAnswers([], [new TraceStep("igdb", "failed", 0)], true);
        });

        retried.Verdict.ShouldBe(ResolutionVerdict.Pending);
    }

    [Fact]
    public async Task A_budget_that_runs_out_after_the_numberless_title_answered_cleanly_is_a_retry_that_finished_and_stays_not_found()
    {
        var (original, retried, _) = await RetryCoverAsync(budget =>
        {
            budget.Cancel();
            return Task.FromResult(ProviderAnswers.None);
        });

        retried.ShouldBeSameAs(original);
    }

    [Fact]
    public async Task A_spine_whose_budget_ran_out_before_the_numberless_title_was_asked_goes_pending()
    {
        var grounding = Grounding(new Catalogue());
        var facts = BookRead("QUIET HARBOR 3 THE LONG WAY");
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        using var budget = new CancellationTokenSource();
        var asks = 0;

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, _ =>
            {
                if (++asks == TrimmedSpans.MaxSpans)
                {
                    budget.Cancel(); // the last span answers cleanly, and then the time is gone
                }

                return Task.FromResult(ProviderAnswers.None);
            }, budget.Token)],
            CancellationToken.None);

        asks.ShouldBe(TrimmedSpans.MaxSpans);
        answers.Single().Verdict.ShouldBe(ResolutionVerdict.Pending);
        answers.Single().Steps.ShouldBe([.. notFound.Steps, new TraceStep("trimmed", "cut_short", 0)]);
    }
}
