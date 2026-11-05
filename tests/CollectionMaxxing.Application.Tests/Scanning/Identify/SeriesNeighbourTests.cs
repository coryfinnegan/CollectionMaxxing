using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A spine that is only a series' subtitle ("NEMESIS", "ILVER FLAMES") finds the wrong book or none, and the series is on the
// spine beside it. The rule names the series to ask with: the words before the colon of a neighbour's best title, or the
// leading words two neighbours' best titles share. Pure. Titles are real series, the reads are the misreads of a stored eval.
public class SeriesNeighbourTests
{
    private static ReadBox At(double x, double y = 0.1) => new(x, y, 0.05, 0.8);

    /// <summary>A spine read that found nothing.</summary>
    private static SeriesRead Lost(string title, ReadBox? box) =>
        new(box, Spine: true, ResolutionVerdict.NotFound, title, BestTitle: null);

    /// <summary>A spine read whose best release is the one given.</summary>
    private static SeriesRead Found(string read, string best, ReadBox? box, ResolutionVerdict verdict = ResolutionVerdict.Matched, double score = 0.95) =>
        new(box, Spine: true, verdict, read, best, score);

    // ---- the series of a title -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Star Trek: First Contact", "Star Trek")]
    [InlineData("Star Trek II: The Wrath of Khan", "Star Trek II")]
    [InlineData("  Dune : Messiah", "Dune")]
    [InlineData("Casablanca", null)]
    [InlineData(": Orphan", null)]
    [InlineData("Up: Adventure", "Up")]
    [InlineData("A: B", null)] // too little to be a name
    [InlineData(null, null)]
    public void The_series_of_a_title_is_the_text_before_its_colon(string? title, string? series) =>
        SeriesNeighbour.SeriesOf(title).ShouldBe(series);

    // ---- one neighbour with a series -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_read_that_found_nothing_asks_with_the_series_of_the_spine_on_its_left()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), Lost("nEMESIS", At(0.36))]);

        series.ShouldBe([null, "Star Trek"]);
    }

    [Fact]
    public void A_read_that_found_nothing_asks_with_the_series_of_the_spine_on_its_right()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Lost("GENERATIONS", At(0.24)), Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30))]);

        series.ShouldBe(["Star Trek", null]);
    }

    [Fact]
    public void An_ambiguous_read_whose_best_release_names_no_series_asks_too()
    {
        // "Nemesis" the book, found for the read "nEMESIS", is the wrong book: the series beside it says which.
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), Found("nEMESIS", "Nemesis", At(0.36), ResolutionVerdict.Ambiguous, 0.7)]);

        series[1].ShouldBe("Star Trek");
    }

    [Fact]
    public void An_ambiguous_neighbour_carries_its_series_too()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30), ResolutionVerdict.Ambiguous, 0.7), Lost("nEMESIS", At(0.36))]);

        series[1].ShouldBe("Star Trek");
    }

    [Fact]
    public void An_ambiguous_read_whose_best_release_already_has_a_series_does_not_ask()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), Found("nEMESIS", "Star Trek: Nemesis", At(0.36), ResolutionVerdict.Ambiguous, 0.7)]);

        series[1].ShouldBeNull();
    }

    [Fact]
    public void An_ambiguous_read_whose_best_release_already_names_the_series_without_a_colon_does_not_ask()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), Found("GENERATIONS", "Star Trek Generations", At(0.36), ResolutionVerdict.Ambiguous, 0.7)]);

        series[1].ShouldBeNull();
    }

    [Fact]
    public void A_matched_read_is_never_asked_again()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), Found("nEMESIS", "Nemesis", At(0.36), ResolutionVerdict.Matched, 0.97)]);

        series.ShouldBe([null, null]);
    }

    [Fact]
    public void A_read_whose_place_another_read_already_holds_matched_is_not_asked()
    {
        var series = SeriesNeighbour.SeriesFor(
            [
                Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)),
                Found("NEMESIS", "Nemesis", At(0.36), ResolutionVerdict.Matched, 0.97),
                Lost("nEMESIS", At(0.37)),
            ]);

        series.ShouldBe([null, null, null]);
    }

    [Fact]
    public void A_read_still_pending_is_not_asked()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), Lost("nEMESIS", At(0.36)) with { Verdict = ResolutionVerdict.Pending }]);

        series[1].ShouldBeNull();
    }

    // ---- no neighbour, no rule -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_lone_spine_has_no_neighbour_and_asks_nothing() =>
        SeriesNeighbour.SeriesFor([Lost("nEMESIS", At(0.36))]).ShouldBe([null]);

    [Fact]
    public void A_neighbour_whose_best_release_has_no_series_gives_nothing_to_ask_with() =>
        SeriesNeighbour.SeriesFor([Found("CASABLANCA", "Casablanca", At(0.30)), Lost("nEMESIS", At(0.36))]).ShouldBe([null, null]);

    [Fact]
    public void A_neighbour_that_found_nothing_gives_nothing_to_ask_with() =>
        SeriesNeighbour.SeriesFor([Lost("STAR TREK FIASI CONTACI", At(0.30)), Lost("nEMESIS", At(0.36))]).ShouldBe([null, null]);

    [Fact]
    public void A_read_with_no_box_has_no_neighbours_and_never_asks() =>
        SeriesNeighbour.SeriesFor([Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), Lost("nEMESIS", null)]).ShouldBe([null, null]);

    [Fact]
    public void A_neighbour_with_no_box_is_nobodys_neighbour() =>
        SeriesNeighbour.SeriesFor([Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", null), Lost("nEMESIS", At(0.36))]).ShouldBe([null, null]);

    [Fact]
    public void A_read_that_is_not_a_spine_asks_nothing()
    {
        var cover = Lost("nEMESIS", At(0.36)) with { Spine = false };

        SeriesNeighbour.SeriesFor([Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), cover]).ShouldBe([null, null]);
    }

    [Fact]
    public void Only_the_spines_next_to_a_read_count_not_one_further_along()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.20)), Found("CASABLANCA", "Casablanca", At(0.26)), Lost("nEMESIS", At(0.32))]);

        series[2].ShouldBeNull();
    }

    [Fact]
    public void A_spine_on_another_row_of_the_shelf_is_no_neighbour()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30, y: 0.6) with { Height = 0.3 }), Lost("nEMESIS", At(0.36) with { Height = 0.3 })]);

        series[1].ShouldBeNull();
    }

    [Fact]
    public void The_other_engines_read_of_the_same_spine_is_the_same_place_not_a_neighbour()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("nEMESIS", "Star Trek: Nemesis", At(0.36)), Lost("nEMESIS", At(0.37))]);

        series[1].ShouldBeNull();
    }

    [Fact]
    public void A_neighbour_the_other_engine_read_too_carries_its_series_through_the_better_read_of_its_place()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Lost("STAR TREK FIASI CONTACI", At(0.30)), Found("STAR TREK FIRST CONTACT", "Star Trek: First Contact", At(0.31)), Lost("nEMESIS", At(0.36))]);

        series[2].ShouldBe("Star Trek");
    }

    // ---- two neighbours ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Leading_words_two_neighbours_share_are_a_series_when_neither_title_has_a_colon()
    {
        var series = SeriesNeighbour.SeriesFor(
            [
                Found("A COURT OF MIST AND FURY", "A Court of Mist and Fury", At(0.20)),
                Lost("ILVER FLAMES", At(0.26)),
                Found("A COURT OF WINGS AND RUIN", "A Court of Wings and Ruin", At(0.32)),
            ]);

        series.ShouldBe([null, "A Court of", null]);
    }

    [Fact]
    public void One_neighbour_alone_gives_no_leading_words_to_share() =>
        SeriesNeighbour.SeriesFor([Found("A COURT OF WINGS AND RUIN", "A Court of Wings and Ruin", At(0.20)), Lost("ILVER FLAMES", At(0.26))]).ShouldBe([null, null]);

    [Fact]
    public void Only_one_leading_word_in_common_is_not_a_series() =>
        SeriesNeighbour.SeriesFor(
            [Found("THE HOBBIT", "The Hobbit", At(0.20)), Lost("ILVER FLAMES", At(0.26)), Found("THE ROAD", "The Road", At(0.32))]).ShouldBe([null, null, null]);

    [Fact]
    public void Two_copies_of_one_title_beside_a_read_are_no_series()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("DUNE MESSIAH", "Dune Messiah", At(0.20)), Lost("ILVER FLAMES", At(0.26)), Found("DUNE MESSIAH", "Dune Messiah", At(0.32))]);

        series[1].ShouldBeNull();
    }

    [Fact]
    public void Leading_words_with_a_word_of_their_own_are_a_series_even_after_an_article()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("THE ART OF WAR", "The Art of War", At(0.20)), Lost("ILVER FLAMES", At(0.26)), Found("THE ART OF LOVE", "The Art of Love", At(0.32))]);

        series[1].ShouldBe("The Art of");
    }

    [Fact]
    public void Leading_words_of_nothing_but_small_words_are_no_series()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("IN THE WOODS", "In the Woods", At(0.20)), Lost("ILVER FLAMES", At(0.26)), Found("IN THE DARK", "In the Dark", At(0.32))]);

        series[1].ShouldBeNull();
    }

    [Fact]
    public void Two_neighbours_of_one_series_give_it_once()
    {
        var series = SeriesNeighbour.SeriesFor(
            [
                Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.20)),
                Lost("nEMESIS", At(0.26)),
                Found("STAR TREK INSURRECTION", "Star Trek: Insurrection", At(0.32)),
            ]);

        series[1].ShouldBe("Star Trek");
    }

    [Fact]
    public void Two_neighbours_of_different_series_give_nothing_to_ask_with()
    {
        var series = SeriesNeighbour.SeriesFor(
            [
                Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.20)),
                Lost("nEMESIS", At(0.26)),
                Found("STAR WARS", "Star Wars: A New Hope", At(0.32)),
            ]);

        series[1].ShouldBeNull();
    }

    [Fact]
    public void Two_series_that_share_their_first_words_ask_with_the_words_they_share()
    {
        var series = SeriesNeighbour.SeriesFor(
            [
                Found("STAR TREK GENERATIONS", "Star Trek: Generations", At(0.20)),
                Lost("nEMESIS", At(0.26)),
                Found("STAR TREK II", "Star Trek II: The Wrath of Khan", At(0.32)),
            ]);

        series[1].ShouldBe("Star Trek");
    }

    [Fact]
    public void A_neighbour_with_a_series_and_one_without_still_give_the_series()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.20)), Lost("nEMESIS", At(0.26)), Found("CASABLANCA", "Casablanca", At(0.32))]);

        series[1].ShouldBe("Star Trek");
    }

    // ---- guards --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_read_that_already_says_the_series_is_not_asked_with_it_again()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), Lost("STAR TREK NEMESIS", At(0.36))]);

        series[1].ShouldBeNull();
    }

    [Fact]
    public void A_read_too_short_to_ask_about_is_not_asked()
    {
        var series = SeriesNeighbour.SeriesFor(
            [Found("STAR TREK FIASI CONTACI", "Star Trek: First Contact", At(0.30)), Lost("AB", At(0.36)), Lost("", At(0.42)), Lost("   ", At(0.48))]);

        series.Skip(1).ShouldAllBe(s => s == null);
    }

    // ---- the ask and the release it may name ---------------------------------------------------------------------------------------

    [Fact]
    public void The_ask_is_the_series_then_the_words_as_read() =>
        SeriesNeighbour.AskTitle("Star Trek", "nEMESIS").ShouldBe("Star Trek nEMESIS");

    [Theory]
    [InlineData("Star Trek: Nemesis", "Star Trek", "nEMESIS", true)]
    [InlineData("Star Trek Generations", "Star Trek", "GENERATIONS", true)]
    [InlineData("A Court of Silver Flames", "A Court of", "ILVER FLAMES", true)] // the misread first letter is still inside the title
    [InlineData("Nemesis", "Star Trek", "nEMESIS", false)] // no series
    [InlineData("Star Trek: First Contact", "Star Trek", "nEMESIS", false)] // not the read's words
    [InlineData("Startrekking: Nemesis", "Star Trek", "nEMESIS", false)] // the series is whole words
    [InlineData("Star Trek II: The Wrath of Khan", "Star Trek", "WRATH OF KHAN", true)]
    [InlineData(null, "Star Trek", "nEMESIS", false)]
    public void A_release_is_named_when_its_title_holds_the_series_and_the_words_read(string? title, string series, string read, bool named) =>
        SeriesNeighbour.Names(title, series, read).ShouldBe(named);
}
