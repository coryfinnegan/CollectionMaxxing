using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// #815: two title choices the server's reader got right and the scorer got wrong. A book read as "Series: Title" ranked the release
// that is only the series name above the book; and of two releases tied on score, the one with the extra subtitle came first because
// the provider listed it first. Public book and film titles, as the issue names them.
public class MatchScorerTitleChoiceTests
{
    private static readonly ScoringOptions Options = new();

    private static CandidateRelease Release(string key, string title, WorkKind kind, int? year = null, params string[] creators) => new()
    {
        Key = key,
        Title = title,
        Kind = kind,
        Year = year,
        Creators = creators,
    };

    private static ReadItem BookRead(string title, string? creator = "Sarah J. Maas") => new() { Title = title, Kind = WorkKind.Book, Creator = creator };

    private static string[] Order(ReadItem read, ScoringProfileKind profile, params CandidateRelease[] releases) =>
        [.. MatchScorer.Evaluate(read, releases, Options.Profile(profile)).Ranked.Select(r => r.Release.Key)];

    // ---- "Series: Title" -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ScoringProfileKind.ShelfSpine)]
    [InlineData(ScoringProfileKind.Cover)]
    public void A_book_read_as_series_and_title_matches_the_book_not_the_series_entry(ScoringProfileKind profile)
    {
        var series = Release("series", "Crescent City", WorkKind.Book, 2022, "Sarah J. Maas");
        var book = Release("book", "House of Earth and Blood", WorkKind.Book, 2020, "Sarah J. Maas");

        Order(BookRead("Crescent City: House of Earth and Blood"), profile, series, book)[0].ShouldBe("book");
    }

    [Fact]
    public void The_credited_score_is_exactly_the_series_subtitle_credit_with_the_author_and_kind()
    {
        var book = Release("book", "House of Earth and Blood", WorkKind.Book, 2020, "Sarah J. Maas");

        var scored = MatchScorer.Evaluate(BookRead("Crescent City: House of Earth and Blood"), [book], Options.Profile(ScoringProfileKind.ShelfSpine)).Ranked.Single();

        scored.Terms.Single(t => t.Name == "title").Value.ShouldBe(0.82);
        scored.Score.ShouldBe(0.8875); // (0.5 x 0.82 + 0.1 + 0.2) / 0.8
    }

    // The verifier's reads (round 1): with no author read, the part after the colon names no other book, and main's top stands. ("The
    // Hobbit" and "There and Back Again" tie at 0.8 on main already, so the order they come in is the one main keeps.)
    [Theory]
    [InlineData("Leaves of Grass: Poems", "Leaves of Grass", "Poems")]
    [InlineData("Watchmen: The Graphic Novel", "Watchmen", "The Graphic Novel")]
    [InlineData("The Wheel of Time: Book One", "The Wheel of Time", "Book One")]
    [InlineData("Sapiens: A Brief History of Humankind", "Sapiens", "A Brief History of Humankind")]
    [InlineData("The Hobbit: There and Back Again", "The Hobbit", "There and Back Again")]
    [InlineData("Emma: A", "Emma", "A")]
    public void Without_an_author_the_part_after_the_colon_earns_no_credit(string read, string main, string after)
    {
        var mainBook = Release("main", main, WorkKind.Book, 1900, "First Author");
        var afterBook = Release("after", after, WorkKind.Book, 2000, "Other Author");

        Order(BookRead(read, creator: null), ScoringProfileKind.ShelfSpine, mainBook, afterBook)[0].ShouldBe("main");
        Order(BookRead(read, creator: null), ScoringProfileKind.Cover, mainBook, afterBook)[0].ShouldBe("main");
    }

    [Fact]
    public void An_author_the_book_does_not_agree_with_earns_no_credit()
    {
        var series = Release("series", "Crescent City", WorkKind.Book, 2022, "Sarah J. Maas");
        var book = Release("book", "House of Earth and Blood", WorkKind.Book, 2020, "Sarah J. Maas");

        var ranked = MatchScorer.Evaluate(BookRead("Crescent City: House of Earth and Blood", "Tess Harrowgate"), [series, book], Options.Profile(ScoringProfileKind.ShelfSpine)).Ranked;

        ranked[0].Release.Key.ShouldBe("series");
        ranked.Single(r => r.Release.Key == "book").Terms.Single(t => t.Name == "title").Value.ShouldBeLessThan(TitleNormalizer.MainTitleCredit);
    }

    [Fact]
    public void A_one_word_subtitle_earns_no_credit_even_from_the_same_author()
    {
        var main = Release("main", "Leaves of Grass", WorkKind.Book, 1855, "Walt Whitman");
        var poems = Release("poems", "Poems", WorkKind.Book, 1900, "Walt Whitman");

        Order(BookRead("Leaves of Grass: Poems", "Walt Whitman"), ScoringProfileKind.ShelfSpine, poems, main)[0].ShouldBe("main");
    }

    [Fact]
    public void The_subtitle_is_what_follows_the_first_colon()
    {
        var thrawn = Release("thrawn", "Thrawn: Alliances", WorkKind.Book, 2018, "Timothy Zahn");

        var scored = MatchScorer.Evaluate(BookRead("Star Wars: Thrawn: Alliances", "Timothy Zahn"), [thrawn], Options.Profile(ScoringProfileKind.ShelfSpine)).Ranked.Single();

        scored.Terms.Single(t => t.Name == "title").Value.ShouldBe(TitleNormalizer.SeriesSubtitleCredit);
    }

    [Fact]
    public void A_longer_title_that_only_holds_the_subtitle_gets_no_full_credit()
    {
        var book = Release("book", "House of Earth and Blood", WorkKind.Book, 2020, "Sarah J. Maas");
        var companion = Release("companion", "House of Earth and Blood Companion", WorkKind.Book, 2021, "Sarah J. Maas");

        var ranked = MatchScorer.Evaluate(BookRead("Crescent City: House of Earth and Blood"), [companion, book], Options.Profile(ScoringProfileKind.ShelfSpine)).Ranked;

        ranked[0].Release.Key.ShouldBe("book");
        ranked.Single(r => r.Release.Key == "companion").Terms.Single(t => t.Name == "title").Value.ShouldBeLessThan(TitleNormalizer.SeriesSubtitleCredit);
    }

    [Fact]
    public void A_book_whose_whole_title_has_the_colon_still_beats_both_halves()
    {
        var whole = Release("whole", "Kitchen Confidential: Adventures in the Culinary Underbelly", WorkKind.Book, 2000, "Anthony Bourdain");
        var first = Release("first", "Kitchen Confidential", WorkKind.Book, 2000, "Anthony Bourdain");
        var second = Release("second", "Adventures in the Culinary Underbelly", WorkKind.Book, 2001, "Anthony Bourdain");
        var read = BookRead("Kitchen Confidential: Adventures in the Culinary Underbelly", "Anthony Bourdain");

        Order(read, ScoringProfileKind.ShelfSpine, first, second, whole)[0].ShouldBe("whole");
    }

    [Fact]
    public void A_book_read_with_a_subtitle_still_finds_its_main_title_when_nothing_is_titled_with_the_subtitle()
    {
        var first = Release("first", "Kitchen Confidential", WorkKind.Book, 2000, "Anthony Bourdain");
        var read = BookRead("Kitchen Confidential: Adventures in the Culinary Underbelly", "Anthony Bourdain");

        var scored = MatchScorer.Evaluate(read, [first], Options.Profile(ScoringProfileKind.ShelfSpine)).Ranked.Single();

        scored.Terms.Single(t => t.Name == "title").Value.ShouldBe(TitleNormalizer.MainTitleCredit);
    }

    [Fact]
    public void The_part_after_the_colon_counts_for_a_book_only()
    {
        // A film's catalogue title carries its franchise ("Star Wars"), so a film titled only with the part after the colon is no better
        // a guess than the franchise film: the main title keeps its lead.
        var franchise = Release("franchise", "Copper Lantern", WorkKind.Film, 1977, "Tess Harrowgate");
        var other = Release("other", "A Quiet Harbor", WorkKind.Film, 2015, "Tess Harrowgate");
        var read = new ReadItem { Title = "Copper Lantern: A Quiet Harbor", Kind = WorkKind.Film, Creator = "Tess Harrowgate" };

        Order(read, ScoringProfileKind.ShelfSpine, other, franchise)[0].ShouldBe("franchise");
    }

    [Fact]
    public void The_part_after_the_colon_stays_under_the_line_where_title_and_kind_alone_would_match()
    {
        TitleNormalizer.SeriesSubtitleCredit.ShouldBeGreaterThan(TitleNormalizer.MainTitleCredit);
        TitleNormalizer.SeriesSubtitleCredit.ShouldBeLessThan(0.845);
    }

    // ---- a tie ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Of_two_candidates_tied_on_score_the_one_closer_to_the_reads_own_words_wins()
    {
        // The film with the extra subtitle and the show of the read's title plus one word tie at 0.67 for a film read; the provider
        // listed the film first.
        var film = Release("film", "Kimba the White Lion: Symphonic Poem", WorkKind.Film, 1991);
        var show = Release("show", "Kimba the White Lion", WorkKind.Tv, 1965);
        var read = new ReadItem { Title = "Kimba the Lion", Kind = WorkKind.Film };

        var ranked = MatchScorer.Evaluate(read, [film, show], Options.Profile(ScoringProfileKind.ShelfSpine)).Ranked;

        ranked[0].Score.ShouldBe(ranked[1].Score); // a true tie
        ranked.Select(r => r.Release.Key).ShouldBe(["show", "film"]);
    }

    [Fact]
    public void Candidates_tied_on_score_and_title_keep_the_order_they_came_in()
    {
        var first = Release("first", "House of Earth and Blood", WorkKind.Book, 2019, "Sarah J. Maas");
        var second = Release("second", "House of Earth and Blood", WorkKind.Book, 2020, "Sarah J. Maas");

        Order(BookRead("House of Earth and Blood"), ScoringProfileKind.ShelfSpine, first, second).ShouldBe(["first", "second"]);
        Order(BookRead("House of Earth and Blood"), ScoringProfileKind.ShelfSpine, second, first).ShouldBe(["second", "first"]);
    }

    [Fact]
    public void A_higher_score_still_beats_a_closer_title()
    {
        // The tie-break is only a tie-break: the read's kind lifts the film above the show of the very same title.
        var show = Release("show", "Kimba the White Lion", WorkKind.Tv, 1965);
        var film = Release("film", "Kimba the White Lion", WorkKind.Film, 1966);
        var read = new ReadItem { Title = "Kimba the White Lion", Kind = WorkKind.Film };

        Order(read, ScoringProfileKind.ShelfSpine, show, film)[0].ShouldBe("film");
    }
}
