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

    private static IdentifyGrounding Grounding(Catalogue catalogue) => Grounding(catalogue, new Providers());

    private static IdentifyGrounding Grounding(Catalogue catalogue, Providers providers) => new(
        new TextSearchResolver(null!, catalogue, [providers], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
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
        var catalogue = new Catalogue(Book("Lantern"));

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", ["XQZV WRTK", "LANTERN", "TESS HARROWGATE"]));

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


        // The name is printed on another line too, but a release titled with the author's own name is the fragment this retry gets past.
        var named = Spine("XQZV WRTK", ["XQZV WRTK", "TESS HARROWGATE", "TESS HARROWGATE STORIES"]);
        SpineLines.PrintedCreator(named, "TESS HARROWGATE", found[2..3]).ShouldBeNull();
    }

    // ---- a release with no creators (a film, a show) is vouched for by the line holding its title ----------------------------------

    private static readonly string[] FilmLines = ["XQZV WRTK", "COPPER LANTERN", "IRON FOX"];

    [Fact]
    public async Task A_spine_line_that_finds_a_film_with_no_creators_is_kept_when_the_line_holds_the_films_title()
    {
        var film = Film("Copper Lantern");
        var catalogue = new Catalogue(film);

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", FilmLines));

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous); // offered, never matched
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Options[0].LocalReleaseId.ShouldBe(film.Release.Id);
        answer.Steps.ShouldContain(s => s.Provider == "line" && s.Outcome.StartsWith("COPPER LANTERN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_one_word_line_that_finds_a_creatorless_release_is_still_not_kept()
    {
        var catalogue = new Catalogue(Film("Lantern"));

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", ["XQZV WRTK", "LANTERN", "IRON FOX"]));

        catalogue.Lookups.ShouldContain("lantern"); // it was asked
        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_creatorless_release_that_the_line_matches_only_loosely_is_not_kept()
    {
        var catalogue = new Catalogue(Film("Copper Lantern Tales From The Far Harbor Lights"))
        {
            Finds = (_, search) => search.NormalizedTitle == "copper lantern",
        };

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", FilmLines));

        catalogue.Lookups.ShouldContain("copper lantern");
        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    [Fact]
    public void A_creatorless_find_is_kept_only_for_two_or_more_title_words_and_a_title_term_of_at_least_three_quarters()
    {
        var facts = Spine("XQZV WRTK", FilmLines);

        SpineLines.KeepsFind(facts, "COPPER LANTERN", "Copper Lantern", [], bestTitleTerm: 0.75).ShouldBeTrue();
        SpineLines.KeepsFind(facts, "COPPER LANTERN", "Copper Lantern", [], bestTitleTerm: 0.74).ShouldBeFalse();
        SpineLines.KeepsFind(facts, "COPPER LANTERN", "Copper Lantern", []).ShouldBeFalse("no title term given");
        SpineLines.KeepsFind(facts, "LANTERN", "Lantern", [], bestTitleTerm: 1.0).ShouldBeFalse("one word");
        SpineLines.KeepsFind(facts, "THE LANTERN", "The Lantern", [], bestTitleTerm: 1.0).ShouldBeFalse("a stop word is not a title word");
    }

    [Fact]
    public async Task A_same_titled_film_with_no_creators_does_not_beat_the_book_whose_author_the_spine_prints_even_when_the_line_holds_the_title_in_two_words()
    {
        var real = BookBy("Copper Lantern", "Tess Harrowgate");
        var catalogue = new Catalogue(Film("Copper Lantern"), BookBy("Copper Lantern", "Mara Quillon"), real);

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", CopperLines, creator: "Iron Fox"));

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options[0].LocalReleaseId.ShouldBe(real.Release.Id);
    }

    [Fact]
    public async Task An_earlier_lines_creatorless_find_does_not_stop_a_later_line_kept_by_its_printed_author()
    {
        var title = BookBy("Iron Fox", "Tess Harrowgate");
        var catalogue = new Catalogue(Film("Copper Lantern Saga"), title);

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", SagaLines, creator: "Mara Quillon"));

        catalogue.Lookups.ShouldContain("iron fox");
        answer.Options[0].LocalReleaseId.ShouldBe(title.Release.Id);
    }

    [Fact]
    public async Task A_printed_authors_release_among_many_the_providers_found_still_comes_first()
    {
        var providers = new Providers(false,
            Known("Copper Lantern", "b1"),
            Known("Copper Lantern", "b2") with { Creators = ["Mara Quillon"] },
            Known("Copper Lantern", "b4") with { Creators = ["Ivy Stone"] },
            Known("Copper Lantern", "b5") with { Creators = ["Ann Field"] },
            Known("Copper Lantern", "b3") with { Creators = ["Tess Harrowgate"] });

        var answer = await GroundAsync(
            Grounding(new Catalogue(), providers), Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN", "TESS HARROWGATE"], creator: "Iron Fox"));

        answer.Options[0].Provider!.ExternalId.ShouldBe("b3");
    }

    [Fact]
    public async Task A_printed_authors_book_in_the_catalogue_past_the_top_three_options_still_comes_first()
    {
        var real = BookBy("Copper Lantern", "Tess Harrowgate");
        var catalogue = new Catalogue(
            Film("Copper Lantern"), Book("Copper Lantern"), BookBy("Copper Lantern", "Mara Quillon"), BookBy("Copper Lantern", "Ivy Stone"), real);

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", CopperLines, creator: "Iron Fox"));

        answer.Options[0].LocalReleaseId.ShouldBe(real.Release.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_creatorless_film_the_line_holds_only_among_other_words_is_not_kept(bool vhs)
    {
        var catalogue = new Catalogue(Film("Copper Lantern")) { Finds = (_, search) => search.NormalizedTitle == "copper lantern tales" };
        var facts = Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN TALES", "IRON FOX"]);

        var answer = await GroundAsync(Grounding(catalogue), vhs ? facts with { Format = ReleaseFormat.Vhs } : facts);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }

    [Fact]
    public async Task A_creatorless_find_never_replaces_what_the_printed_author_re_score_kept()
    {
        var title = BookBy("Iron Fox", "Tess Harrowgate");
        var catalogue = new Catalogue(Film("Copper Lantern Saga"), Film("Iron Fox"), title);
        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", SagaLines, creator: "Mara Quillon"));
        answer.Options[0].LocalReleaseId.ShouldBe(title.Release.Id);
    }

    [Fact]
    public async Task A_printed_author_among_the_providers_answers_past_the_options_blocks_a_creatorless_find()
    {
        var providers = new Providers(false,
            Known("Copper Lantern", "b1"), Known("Copper Lantern", "b2") with { Year = 1999 }, Known("Copper Lantern", "b4") with { Year = 2005 },
            Known("Copper Harbor Lantern Light", "b3") with { Creators = ["Tess Harrowgate"] })
        {
            Finds = (title, _) => string.Equals(title, "COPPER LANTERN", StringComparison.OrdinalIgnoreCase),
        };
        var answer = await GroundAsync(Grounding(new Catalogue(), providers), Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN", "TESS HARROWGATE"], creator: "Iron Fox"));
        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }

    // ---- C runs after every line has been asked ----------------------------------------------------------------------------------

    private static readonly string[] SagaLines = ["XQZV WRTK", "COPPER LANTERN SAGA", "TESS HARROWGATE", "IRON FOX"];

    [Fact]
    public async Task A_later_line_that_is_kept_as_asked_wins_over_an_earlier_line_kept_only_through_its_printed_author()
    {
        var series = BookBy("Copper Lantern Saga", "Tess Harrowgate");
        var title = BookBy("Iron Fox", "Tess Harrowgate");
        var catalogue = new Catalogue(Film("Copper Lantern Saga"), series, title);

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", SagaLines, creator: "Mara Quillon"));

        catalogue.Lookups.ShouldContain("iron fox"); // the retry did not stop at the saga line
        answer.Options[0].LocalReleaseId.ShouldBe(title.Release.Id);
        answer.Steps.ShouldContain(s => s.Provider == "line" && s.Outcome.StartsWith("IRON FOX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Of_several_lines_kept_only_through_their_printed_author_the_strongest_is_the_find()
    {
        var weaker = BookBy("Copper Lantern Sagas", "Tess Harrowgate");
        var stronger = BookBy("Iron Fox Gate", "Tess Harrowgate");
        var catalogue = new Catalogue(Film("Copper Lantern Saga"), Film("Iron Fox Gate"), weaker, stronger)
        {
            Finds = (book, search) => search.NormalizedTitle switch
            {
                "copper lantern saga" => book.Work.NormalizedTitle.StartsWith("copper lantern saga", StringComparison.Ordinal),
                "iron fox gate" => book.Work.NormalizedTitle.StartsWith("iron fox gate", StringComparison.Ordinal),
                _ => false,
            },
        };
        var lines = new[] { "XQZV WRTK", "COPPER LANTERN SAGA", "IRON FOX GATE", "TESS HARROWGATE" };

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", lines, creator: "Mara Quillon"));

        answer.Options[0].LocalReleaseId.ShouldBe(stronger.Release.Id);
        answer.Steps.ShouldContain(s => s.Provider == "line" && s.Outcome.StartsWith("IRON FOX GATE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_spine_whose_budget_runs_out_before_every_line_is_asked_is_pending_not_judged_by_the_lines_it_did_ask()
    {
        var catalogue = new Catalogue(Film("Copper Lantern Saga"), BookBy("Copper Lantern Saga", "Tess Harrowgate"));
        var grounding = Grounding(catalogue);
        var facts = Spine("XQZV WRTK", SagaLines, creator: "Mara Quillon");
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        using var budget = new CancellationTokenSource();

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, async _ =>
            {
                await budget.CancelAsync(); // the first line's ask finished, the time is gone before the next
                return ProviderAnswers.None;
            }, budget.Token)],
            CancellationToken.None);

        answers.Single().Verdict.ShouldBe(ResolutionVerdict.Pending);
    }

    [Fact]
    public async Task A_spine_whose_line_asks_were_all_made_but_one_provider_failed_stays_pending_not_revived_by_its_printed_author()
    {
        var grounding = Grounding(new Catalogue());
        var facts = Spine("XQZV WRTK", ["XQZV WRTK", "TESS HARROWGATE", "COPPER LANTERN"], creator: "Iron Fox");
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        using var budget = new CancellationTokenSource();

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, ask =>
            {
                // The author's name line is asked first and its provider fails; the title line is answered, by a creatorless release among others.
                if (ask.Title != "COPPER LANTERN")
                {
                    return Task.FromResult(new ProviderAnswers([], [], true));
                }

                return Task.FromResult(new ProviderAnswers(
                    [Known("Copper Lantern", "b1"), Known("Copper Lantern", "b2") with { Creators = ["Mara Quillon"] }, Known("Copper Lantern", "b3") with { Creators = ["Tess Harrowgate"] }],
                    [],
                    false));
            }, budget.Token)],
            CancellationToken.None);

        answers.Single().Verdict.ShouldBe(ResolutionVerdict.Pending);
    }

    [Fact]
    public async Task A_creatorless_release_titled_exactly_like_the_line_is_not_kept_by_the_printed_author_re_score()
    {
        // Scored with the printed author the film still tops the answer (its title is the line's); the author's other book does not take its place.
        var catalogue = new Catalogue(Film("Copper Lantern"), BookBy("Copper Harbor Lantern Light", "Tess Harrowgate"))
        {
            Finds = (_, search) => search.NormalizedTitle == "copper lantern",
        };

        var answer = await GroundAsync(Grounding(catalogue), Spine("XQZV WRTK", CopperLines, creator: "Iron Fox"));

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
    }

    // ---- the same, from the providers' answers (the shape of a real shelf: no catalogue hit) ---------------------------------------

    [Fact]
    public async Task A_line_is_judged_by_the_author_its_spine_prints_among_the_providers_answers_too()
    {
        var providers = new Providers(false,
            Known("Copper Lantern", "b1"),
            Known("Copper Lantern", "b2") with { Creators = ["Mara Quillon"] },
            Known("Copper Lantern", "b3") with { Creators = ["Tess Harrowgate"] });

        var answer = await GroundAsync(
            Grounding(new Catalogue(), providers), Spine("XQZV WRTK", ["XQZV WRTK", "COPPER LANTERN", "TESS HARROWGATE"], creator: "Iron Fox"));

        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Options[0].Provider!.ExternalId.ShouldBe("b3");
        answer.Steps.ShouldContain(s => s.Provider == "line");
    }

    [Fact]
    public async Task A_swap_that_ties_the_name_reads_own_best_ends_the_retries_and_goes_first()
    {
        // The swap's find scores exactly what the name read's own best does: it goes first, and the lines are not asked.
        var swapped = BookBy("Quiet Harbor", "Quiet Harbor");
        var catalogue = NameCatalogue(swapped, BookBy("Copper Lantern", "Tess Harrowgate"));

        var answer = await GroundAsync(Grounding(catalogue), NameSpine());

        catalogue.Lookups.ShouldBe(["tess harrowgate", "quiet harbor"]);
        answer.Options[0].LocalReleaseId.ShouldBe(swapped.Release.Id);
        answer.Steps.ShouldContain(s => s.Provider == "swapped");
        answer.Steps.ShouldNotContain(s => s.Provider == "line");
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
