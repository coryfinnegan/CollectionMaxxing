using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A spine read that finds nothing is asked again without the stray words at its ends ("GARY QUIET HARBOR NOVEL" is the book
// "Quiet Harbor" with an author's first name in front and a label behind). The release found that way is offered for the person
// to pick, never matched, so a trimmed find can neither add a book by itself nor invent a title. Titles are neutral.
public class IdentifyGroundingTrimmedRetryTests
{
    private const string Spine = "GARY QUIET HARBOR NOVEL";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static ReadFacts Read(string title = Spine) => new(
        title, title, "Gary Someone", null, null, WorkKind.Book, null, null, null);

    private static IdentifyGrounding Grounding(Catalogue catalogue, Providers? providers = null) => new(
        new TextSearchResolver(null!, catalogue, [providers ?? new Providers()], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    private static Task<BarcodeResolution> GroundAsync(
        IdentifyGrounding grounding, ScoringProfileKind profile = ScoringProfileKind.ShelfSpine, string title = Spine) =>
        grounding.GroundAsync(Read(title), profile, Budget, CancellationToken.None);

    [Fact]
    public async Task A_spine_read_that_finds_nothing_comes_back_ambiguous_with_the_release_a_trimmed_span_found()
    {
        var book = Book("Quiet Harbor");
        var catalogue = new Catalogue(book);

        var answer = await GroundAsync(Grounding(catalogue));

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options.Single().LocalReleaseId.ShouldBe(book.Release.Id);
    }

    [Fact]
    public async Task The_trace_says_which_span_found_it_and_which_words_were_dropped()
    {
        var answer = await GroundAsync(Grounding(new Catalogue(Book("Quiet Harbor"))));

        answer.Steps.ShouldContain(new TraceStep("trimmed", "QUIET HARBOR (dropped: GARY, NOVEL)", 0));
    }

    [Fact]
    public async Task The_trace_keeps_the_steps_of_the_lookup_of_the_whole_read_before_the_trimmed_one()
    {
        var answer = await GroundAsync(Grounding(new Catalogue(Book("Quiet Harbor"))));

        var steps = answer.Steps.Select(s => s.Provider).ToList();
        steps.IndexOf("catalogue").ShouldBeLessThan(steps.IndexOf("trimmed"));
        answer.Steps.Count(s => s.Provider == "catalogue").ShouldBe(2); // the whole read, then the span that found it
        steps.Last().ShouldBe("profile");
    }

    [Fact]
    public async Task The_spans_are_asked_in_order_and_asking_stops_at_the_first_that_finds_a_release()
    {
        var catalogue = new Catalogue(Book("Quiet Harbor"));

        await GroundAsync(Grounding(catalogue));

        // The whole read, then one off the start, one off the end, one off each end (this finds it). Two off the start is never asked.
        catalogue.Lookups.ShouldBe(["gary quiet harbor novel", "quiet harbor novel", "gary quiet harbor", "quiet harbor"]);
    }

    [Fact]
    public async Task A_span_is_scored_as_the_reads_title_so_a_release_it_finds_would_have_matched_but_is_only_offered()
    {
        var answer = await GroundAsync(Grounding(new Catalogue(Book("Quiet Harbor"))));

        answer.Options.Single().Score.ShouldBeGreaterThanOrEqualTo(new ScoringThresholds().MatchedScore); // the scorer would say Matched
        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous); // a trimmed find is never matched
    }

    [Fact]
    public async Task The_whole_read_alone_finds_nothing_in_the_same_catalogue()
    {
        // Control: with the retry out of the picture the stray words sink the read, so it is the retry that found the book.
        var answer = await GroundAsync(Grounding(new Catalogue(Book("Quiet Harbor"))), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }

    [Theory]
    [InlineData(ScoringProfileKind.Cover)]
    [InlineData(ScoringProfileKind.BareLabel)]
    [InlineData(ScoringProfileKind.LooseCartridge)]
    [InlineData(ScoringProfileKind.BarcodeProduct)]
    public async Task A_read_scored_with_any_profile_but_the_spine_one_does_not_retry(ScoringProfileKind profile)
    {
        var catalogue = new Catalogue(Book("Quiet Harbor"));

        var answer = await GroundAsync(Grounding(catalogue), profile);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldBe(["gary quiet harbor novel"]);
        answer.Steps.ShouldNotContain(s => s.Provider == "trimmed");
    }

    [Fact]
    public async Task A_read_a_provider_could_not_answer_is_pending_and_does_not_retry()
    {
        var catalogue = new Catalogue(Book("Quiet Harbor"));

        var answer = await GroundAsync(Grounding(catalogue, new Providers(down: true)));

        answer.Verdict.ShouldBe(ResolutionVerdict.Pending);
        catalogue.Lookups.ShouldBe(["gary quiet harbor novel"]);
    }

    [Fact]
    public async Task A_read_that_already_matched_does_not_retry()
    {
        var catalogue = new Catalogue(Book("Quiet Harbor"));

        var matched = await GroundAsync(Grounding(catalogue), title: "Quiet Harbor");

        matched.Verdict.ShouldBe(ResolutionVerdict.Matched);
        catalogue.Lookups.ShouldBe(["quiet harbor"]);
        matched.Steps.ShouldNotContain(s => s.Provider == "trimmed");
    }

    [Fact]
    public async Task A_read_of_two_words_is_never_retried()
    {
        var catalogue = new Catalogue(Book("Harbor"));

        var answer = await GroundAsync(Grounding(catalogue), title: "NOVEL HARBOR");

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldBe(["novel harbor"]);
    }

    [Fact]
    public async Task A_read_none_of_whose_spans_finds_a_release_stays_not_found_after_at_most_four_spans()
    {
        var catalogue = new Catalogue();

        var answer = await GroundAsync(Grounding(catalogue), title: "ONE TWO THREE FOUR FIVE SIX SEVEN EIGHT");

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Options.ShouldBeEmpty();
        answer.Steps.ShouldNotContain(s => s.Provider == "trimmed");
        catalogue.Lookups.Count.ShouldBe(1 + TrimmedSpans.MaxSpans);
        catalogue.Lookups.Distinct().Count().ShouldBe(catalogue.Lookups.Count);
    }

    [Fact]
    public async Task The_answer_of_a_read_no_span_helps_is_the_one_it_had_untouched()
    {
        var grounding = Grounding(new Catalogue());
        var facts = Read();
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, _ => Task.FromResult(ProviderAnswers.None), CancellationToken.None)],
            CancellationToken.None);

        answers.Single().ShouldBeSameAs(notFound);
    }

    [Fact]
    public async Task A_span_is_asked_about_with_the_reads_other_facts_and_what_the_read_said_it_read_stays_the_original_text()
    {
        var grounding = Grounding(new Catalogue());
        var facts = Read() with { Format = ReleaseFormat.Paperback, Year = 2015, KindContext = WorkKind.Book };
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        var asked = new List<ReadFacts>();

        await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, f => { asked.Add(f); return Task.FromResult(ProviderAnswers.None); }, CancellationToken.None)],
            CancellationToken.None);

        asked.Select(f => f.Title).ShouldBe(["QUIET HARBOR NOVEL", "GARY QUIET HARBOR", "QUIET HARBOR", "HARBOR NOVEL"]);
        asked.ShouldAllBe(f => f.Text == Spine);
        asked.ShouldAllBe(f => f.Creator == "Gary Someone" && f.Kind == WorkKind.Book && f.Format == ReleaseFormat.Paperback && f.Year == 2015 && f.KindContext == WorkKind.Book);
        facts.Title.ShouldBe(Spine);
    }

    [Fact]
    public async Task A_read_whose_budget_is_spent_keeps_its_not_found()
    {
        var catalogue = new Catalogue(Book("Quiet Harbor"));
        var grounding = Grounding(catalogue);
        var facts = Read();
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        using var spent = new CancellationTokenSource();
        await spent.CancelAsync();

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, _ => Task.FromResult(ProviderAnswers.None), spent.Token)],
            CancellationToken.None);

        answers.Single().ShouldBeSameAs(notFound);
        catalogue.Lookups.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_budget_that_runs_out_during_the_retry_ends_it_and_the_not_found_stands()
    {
        var catalogue = new Catalogue(Book("Quiet Harbor"));
        var grounding = Grounding(catalogue);
        var facts = Read();
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        using var budget = new CancellationTokenSource();

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, async _ =>
            {
                await budget.CancelAsync(); // the first span's ask uses up the time that was left
                return new ProviderAnswers([], [new TraceStep("openlibrary", "failed", 0)], true);
            }, budget.Token)],
            CancellationToken.None);

        answers.Single().ShouldBeSameAs(notFound);
        catalogue.Lookups.ShouldBe(["quiet harbor novel"]); // one span was looked for; the later ones, which would have found it, were not
    }

    [Fact]
    public async Task Reads_retry_together_one_catalogue_call_per_round_and_only_for_the_reads_still_looking()
    {
        var catalogue = new Catalogue(Book("Quiet Harbor"), Book("Winter Garden"));
        var grounding = Grounding(catalogue);
        var first = Read("GARY QUIET HARBOR NOVEL"); // found by its third span
        var second = Read("ANNA WINTER GARDEN"); // found by its first span
        var third = Read("ONE TWO THREE FOUR"); // never found
        var answers = new[] { first, second, third }
            .Select(f => grounding.Score(f, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None))
            .ToArray();

        var retried = await grounding.RetryTrimmedAsync(
            [.. new[] { first, second, third }.Select((f, i) =>
                new TrimmedRetry(f, ScoringProfileKind.ShelfSpine, answers[i], _ => Task.FromResult(ProviderAnswers.None), CancellationToken.None))],
            CancellationToken.None);

        retried.Select(a => a.Verdict).ShouldBe([ResolutionVerdict.Ambiguous, ResolutionVerdict.Ambiguous, ResolutionVerdict.NotFound]);
        catalogue.Batches.Count.ShouldBe(4);
        catalogue.Batches[0].ShouldBe(["quiet harbor novel", "winter garden", "two three four"]); // 1 off the start of each read
        catalogue.Batches[1].ShouldBe(["gary quiet harbor", "one two three"]); // the second read found its book in round one and asks no more
        catalogue.Batches[2].ShouldBe(["quiet harbor", "two three"]);
        catalogue.Batches[3].ShouldBe(["three four"]); // the first found its book in round three
    }

    [Fact]
    public async Task A_read_with_nothing_to_search_is_not_retried()
    {
        var grounding = Grounding(new Catalogue());
        var facts = Read(title: "  ") with { Text = null };

        var answer = await grounding.GroundAsync(facts, ScoringProfileKind.ShelfSpine, Budget, CancellationToken.None);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Steps.ShouldNotContain(s => s.Provider == "trimmed");
    }
}
