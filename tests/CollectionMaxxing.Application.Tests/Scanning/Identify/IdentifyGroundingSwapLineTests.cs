using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A spine whose creator swap found only a release with no creators (a film, a bare book edition) asks one title line of its piece,
// so the spine's own title is heard too. The swap's find stands unless that line's find is kept like any line's find, or holds the swap's
// title. Provider answers are stubbed by the text asked. Names and most titles are neutral.
public class IdentifyGroundingSwapLineTests
{
    private static readonly string[] JoeLines = ["VOLCANO", "JOE VERSUS THE", "VERSUS THE", "TOM HANKS", "MEG RYAN", "COLLECTOR"];

    private static IdentifyGrounding Grounding() => new(
        new TextSearchResolver(null!, new Catalogue(), [new Providers()], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    private static ProviderRelease Release(string title, string id, WorkKind kind, params string[] creators) => new()
    {
        Provider = "stub",
        ExternalId = id,
        Title = title,
        Kind = kind,
        Format = kind == WorkKind.Film ? ReleaseFormat.Vhs : ReleaseFormat.Paperback,
        Year = 1990,
        Creators = creators,
    };

    private static ProviderRelease FilmOf(string title, string id = "f1") => Release(title, id, WorkKind.Film);

    private static ReadFacts Spine(string title, string creatorAsRead, string[] lines) => new(
        title, string.Join(' ', lines), null, null, null, null, null, null, null,
        CreatorAsRead: creatorAsRead, Lines: SpineLines.Join(lines));

    /// <summary>Retries the read with providers that answer by the text asked (a text not in <paramref name="answers"/> finds nothing); returns the answer and every text asked.</summary>
    private static async Task<(BarcodeResolution Answer, List<string> Asked)> RetryAsync(
        ReadFacts facts, Dictionary<string, ProviderRelease[]> answers, bool failOn = false, string? failingText = null)
    {
        var grounding = Grounding();
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        var asked = new List<string>();
        var retried = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, ask =>
            {
                asked.Add(ask.Title ?? string.Empty);
                if (failOn && string.Equals(ask.Title, failingText, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new ProviderAnswers([], [], true));
                }

                var found = answers.FirstOrDefault(a => string.Equals(a.Key, ask.Title, StringComparison.OrdinalIgnoreCase)).Value ?? [];
                return Task.FromResult(new ProviderAnswers(found, [], false));
            }, CancellationToken.None)],
            CancellationToken.None);
        return (retried.Single(), asked);
    }

    [Fact]
    public async Task A_spine_whose_swap_found_a_film_with_no_creators_asks_its_title_line_too_and_offers_the_film_the_line_finds_when_its_title_holds_the_swaps()
    {
        var facts = Spine("ЛOZ DIWOJ", "VOLCANO", JoeLines);
        var (answer, asked) = await RetryAsync(facts, new()
        {
            ["VOLCANO"] = [FilmOf("Volcano")],
            ["JOE VERSUS THE"] =
            [
                FilmOf("Joe Versus the Volcano", "f2"),
                Release("Savage in Limbo", "b1", WorkKind.Book, "John Patrick Shanley"),
                Release("Danny and the Deep Blue Sea", "b2", WorkKind.Book, "John Patrick Shanley"),
            ],
        });

        answer.Options[0].Provider!.Title.ShouldBe("Joe Versus the Volcano");
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Steps.ShouldContain(s => s.Provider == "line");
        asked.Count(text => JoeLines.Contains(text) && text != "VOLCANO").ShouldBe(1);
    }

    [Fact]
    public async Task A_swaps_film_with_no_creators_stays_when_its_spines_line_finds_another_film_with_no_creators()
    {
        var facts = Spine("Frigd faroan fomatoes", "EXCALIBUR", ["EXCALIBUR", "fried Creca fomatoes"]);
        var (answer, _) = await RetryAsync(facts, new()
        {
            ["EXCALIBUR"] = [FilmOf("Excalibur")],
            ["fried Creca fomatoes"] = [FilmOf("Fried Green Tomatoes", "f2")],
        });

        answer.Options[0].Provider!.Title.ShouldBe("Excalibur");
        answer.Steps.ShouldContain(s => s.Provider == "swapped");
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_swaps_book_with_no_creators_stays_when_the_line_is_its_authors_name_misread()
    {
        var facts = Spine("nextotKin", "nextofKin", ["nextofKin", "Khah Bonam-Young"]);
        var (answer, _) = await RetryAsync(facts, new()
        {
            ["nextofKin"] = [Release("Next of Kin", "b1", WorkKind.Book)],
            ["Khah Bonam-Young"] =
            [
                Release("Before We Were Strangers", "b2", WorkKind.Book, "Hannah Bonam-Young"),
                Release("Out of Office", "b3", WorkKind.Book, "Hannah Bonam-Young"),
            ],
        });

        answer.Options[0].Provider!.Title.ShouldBe("Next of Kin");
    }

    [Fact]
    public async Task Only_one_line_is_asked_after_a_creatorless_swap_find()
    {
        var facts = Spine("ЛOZ DIWOJ", "VOLCANO", [.. JoeLines, "SAVAGE ISLAND LIFE"]);
        var (_, asked) = await RetryAsync(facts, new() { ["VOLCANO"] = [FilmOf("Volcano")] });

        asked.Count(text => text is not ("VOLCANO" or "ЛOZ DIWOJ")).ShouldBe(1);
    }

    [Fact]
    public async Task A_swap_whose_find_has_creators_asks_no_line()
    {
        var facts = Spine("Landlne", "LANDLINE", ["LANDLINE", "JOE VERSUS THE"]);
        var (answer, asked) = await RetryAsync(facts, new() { ["LANDLINE"] = [Release("Landline", "b1", WorkKind.Book, "Rainbow Rowell")] });

        asked.ShouldNotContain("JOE VERSUS THE");
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_trimmed_spans_creatorless_find_still_ends_the_retry()
    {
        var facts = new ReadFacts(
            "MIDNIGHT SOIL V.E. SCHWAB", "MIDNIGHT SOIL V.E. SCHWAB", null, null, null, null, null, null, null,
            Lines: SpineLines.Join(["MIDNIGHT SOIL V.E. SCHWAB", "JOE VERSUS THE", "TOM HANKS"]));
        var (answer, asked) = await RetryAsync(facts, new() { ["MIDNIGHT SOIL"] = [FilmOf("The Soil")], ["MIDNIGHT SOIL V.E."] = [FilmOf("The Soil")] });

        asked.ShouldNotContain("JOE VERSUS THE");
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_line_the_providers_cannot_answer_leaves_the_swaps_find_not_pending()
    {
        var facts = Spine("ЛOZ DIWOJ", "VOLCANO", JoeLines);
        var (answer, _) = await RetryAsync(
            facts, new() { ["VOLCANO"] = [FilmOf("Volcano")] }, failOn: true, failingText: "JOE VERSUS THE");

        answer.Verdict.ShouldNotBe(ResolutionVerdict.Pending);
        answer.Options[0].Provider!.Title.ShouldBe("Volcano");
        answer.Steps.ShouldContain(s => s.Provider == "swapped");
    }

    [Fact]
    public async Task A_budget_that_runs_out_before_the_line_leaves_the_swaps_find()
    {
        var facts = Spine("ЛOZ DIWOJ", "VOLCANO", JoeLines);
        var grounding = Grounding();
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        using var budget = new CancellationTokenSource();

        var retried = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, async ask =>
            {
                if (string.Equals(ask.Title, "VOLCANO", StringComparison.OrdinalIgnoreCase))
                {
                    await budget.CancelAsync(); // the swap's ask finished, the time is gone before the line
                    return new ProviderAnswers([FilmOf("Volcano")], [], false);
                }

                return ProviderAnswers.None;
            }, budget.Token)],
            CancellationToken.None);

        retried.Single().Verdict.ShouldNotBe(ResolutionVerdict.Pending);
        retried.Single().Options[0].Provider!.Title.ShouldBe("Volcano");
    }

    // ---- SpineLines: which line is a title, and when one title holds another ---------------------------------------------------------

    [Fact]
    public void A_role_line_a_line_of_one_word_the_creator_as_read_and_a_line_like_a_text_the_read_was_asked_as_are_never_title_like()
    {
        var facts = Spine("cnoi Emergency Contact o", "THE KNAVE", ["TWO AUTHORS.", "COLLECTOR", "THE KNAVE", "croifmergency Contact os", "JOE VERSUS THE"]);

        SpineLines.TitleLike(facts, "TWO AUTHORS.", []).ShouldBeFalse();
        SpineLines.TitleLike(facts, "BESTSELLING AUTHOR HERE", []).ShouldBeFalse();
        SpineLines.TitleLike(facts, "COLLECTOR", []).ShouldBeFalse();
        SpineLines.TitleLike(facts, "THE KNAVE", []).ShouldBeFalse();
        SpineLines.TitleLike(facts, "croifmergency Contact os", ["cnoi Emergency Contact o"]).ShouldBeFalse();
        SpineLines.TitleLike(facts, "JOE VERSUS THE", []).ShouldBeTrue();
    }

    [Fact]
    public void A_title_holds_another_when_every_title_word_of_the_other_is_in_it()
    {
        SpineLines.HoldsTitle("Joe Versus the Volcano", "Joe Versus the Volcano: The Film").ShouldBeTrue();
        SpineLines.HoldsTitle("Fried Green Tomatoes", "Excalibur").ShouldBeFalse();
        SpineLines.HoldsTitle("Excalibur", "Fried Green Tomatoes").ShouldBeFalse();
        SpineLines.HoldsTitle(null, "Anything Here").ShouldBeFalse();
    }

    [Fact]
    public void A_one_word_title_is_held_by_a_longer_title_that_has_it()
    {
        SpineLines.HoldsTitle("Volcano", "Joe Versus the Volcano").ShouldBeTrue();
        SpineLines.HoldsTitle("Volcano", "Joe Versus the Mountain").ShouldBeFalse();
    }
}
