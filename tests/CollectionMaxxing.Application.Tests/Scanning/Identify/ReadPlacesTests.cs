using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// One item per place. The two engines read the same shelf and often read the same spine: one place holds one thing, whatever
// each read made of it, so the read that grounds better is the row shown and the other is skipped. A piece covering several
// spines (Vision sometimes makes one of a run of them) is not a place: the reads inside it are the spines, and each keeps its
// own. One engine's own two reads of a place never contest it.
public class ReadPlacesTests
{
    /// <summary>A read of the Vision pipeline's: a spine <paramref name="width"/> wide whose left edge is at <paramref name="x"/>.</summary>
    private static PlacedRead Vision(double x, ReadGrounding grounding = ReadGrounding.Matched, double score = 0.9, double width = 0.05) =>
        new(new ReadBox(x, 0.1, width, 0.8), grounding, score);

    /// <summary>A read only the picture model made, at the same kind of place.</summary>
    private static PlacedRead Model(double x, ReadGrounding grounding = ReadGrounding.Matched, double score = 0.9, double width = 0.05) =>
        Vision(x, grounding, score, width) with { Unconfirmed = true };

    /// <summary>The answer a line of the spine found: offered (Ambiguous, flagged), its release scoring <paramref name="score"/>, with the "line" step.</summary>
    private static BarcodeResolution FoundByLine(double score) => new(
        ResolutionVerdict.Ambiguous,
        [new ResolutionOption(null, TrimmedRetryHarness.Known("Iron Fox"), score)],
        null,
        [new TraceStep(SpineLines.StepName, "IRON FOX (title read: XQZV WRTK)", 0)])
    { FoundByTrimmedSpan = true };

    [Fact]
    public void A_spine_revived_by_a_line_never_takes_the_place_of_a_model_read_at_the_same_place()
    {
        var revived = ReadPlaces.Of(FoundByLine(1.0), new ReadBox(0.30, 0.1, 0.05, 0.8), unconfirmed: false);
        var model = Model(0.31, ReadGrounding.Ambiguous, 0.90);

        ReadPlaces.Weaker([revived, model]).ShouldBe([0]); // the model's read stands, though the revival scored higher
    }

    [Fact]
    public void A_spine_found_by_a_trimmed_span_still_contends_with_what_it_found()
    {
        var offered = FoundByLine(1.0) with { Steps = [new TraceStep("trimmed", "IRON FOX (dropped: XQZV)", 0)] };

        ReadPlaces.Weaker([ReadPlaces.Of(offered, new ReadBox(0.30, 0.1, 0.05, 0.8), unconfirmed: false), Model(0.31, ReadGrounding.Ambiguous, 0.90)]).ShouldBe([1]);
    }

    [Fact]
    public void The_read_that_matched_takes_the_place_from_one_that_only_narrowed_it_down()
    {
        ReadPlaces.Weaker([Vision(0.30, ReadGrounding.Ambiguous, 0.95), Model(0.31, ReadGrounding.Matched, 0.80)]).ShouldBe([0]);
    }

    [Fact]
    public void A_matched_read_keeps_its_place_against_a_later_ambiguous_one()
    {
        ReadPlaces.Weaker([Vision(0.30, ReadGrounding.Matched, 0.80), Model(0.31, ReadGrounding.Ambiguous, 0.95)]).ShouldBe([1]);
    }

    [Fact]
    public void Between_two_matched_reads_of_one_place_the_higher_score_stands()
    {
        ReadPlaces.Weaker([Vision(0.30, score: 0.80), Model(0.31, score: 0.95)]).ShouldBe([0]);
        ReadPlaces.Weaker([Vision(0.30, score: 0.95), Model(0.31, score: 0.80)]).ShouldBe([1]);
    }

    [Fact]
    public void A_tie_keeps_the_earlier_read_so_Visions_own_stands_where_the_model_read_no_better()
    {
        ReadPlaces.Weaker([Vision(0.30, score: 0.90), Model(0.31, score: 0.90)]).ShouldBe([1]);
    }

    [Fact]
    public void A_read_that_found_nothing_loses_its_place_to_one_that_matched_whichever_came_first()
    {
        ReadPlaces.Weaker([Vision(0.30, ReadGrounding.Nothing, 0), Model(0.31, ReadGrounding.Matched)]).ShouldBe([0]);
        ReadPlaces.Weaker([Vision(0.30, ReadGrounding.Matched), Model(0.31, ReadGrounding.Nothing, 0)]).ShouldBe([1]);
    }

    [Fact]
    public void Two_reads_that_both_found_nothing_are_left_alone_because_there_is_no_better_read_to_pick()
    {
        ReadPlaces.Weaker([Vision(0.30, ReadGrounding.Nothing, 0), Model(0.31, ReadGrounding.Nothing, 0)]).ShouldBeEmpty();
    }

    [Fact]
    public void Reads_in_separate_places_both_stand_however_they_read()
    {
        ReadPlaces.Weaker([Vision(0.10, ReadGrounding.Nothing, 0), Model(0.60, ReadGrounding.Matched)]).ShouldBeEmpty();
    }

    [Fact]
    public void Spines_that_only_touch_edge_to_edge_are_separate_places()
    {
        ReadPlaces.Weaker([Vision(0.10), Model(0.15)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_read_with_no_box_has_no_place_so_it_neither_takes_one_nor_loses_one()
    {
        ReadPlaces.Weaker([Vision(0.30) with { Box = null }, Model(0.30)]).ShouldBeEmpty();
        ReadPlaces.Weaker([Vision(0.30), Model(0.30) with { Box = null }]).ShouldBeEmpty();
    }

    [Fact]
    public void A_read_still_being_worked_out_keeps_its_place_and_takes_none()
    {
        ReadPlaces.Weaker([Vision(0.30, ReadGrounding.Unsettled, 0), Model(0.31, ReadGrounding.Matched)]).ShouldBeEmpty();
        ReadPlaces.Weaker([Vision(0.30, ReadGrounding.Matched), Model(0.31, ReadGrounding.Unsettled, 0)]).ShouldBeEmpty();
    }

    [Fact]
    public void Two_model_reads_of_a_place_take_it_from_a_weaker_Vision_read_and_each_of_them_stands()
    {
        // Vision loses its place to the first read that beats it; the model's own second read of the place is a repeat of its
        // own, which RepeatReads settles by release, not a second engine disagreeing.
        ReadPlaces.Weaker([Vision(0.30, ReadGrounding.Ambiguous, 0.8), Model(0.31, score: 0.93), Model(0.32, score: 0.97)]).ShouldBe([0]);
    }

    [Fact]
    public void The_stronger_of_two_reads_in_one_place_stands_and_a_read_elsewhere_stands_too()
    {
        ReadPlaces.Weaker([Vision(0.10, ReadGrounding.Ambiguous, 0.7), Model(0.11, ReadGrounding.Matched), Model(0.60)]).ShouldBe([0]);
    }

    [Fact]
    public void A_read_takes_a_place_by_beating_every_read_standing_there_so_one_spine_is_left_with_one_row()
    {
        // Vision read this spine twice (a repeat of its own, which the two reads do not contest), and the model's read beats both.
        var reads = new[] { Vision(0.30, score: 0.80), Vision(0.31, ReadGrounding.Ambiguous, 0.95), Model(0.305, score: 0.90) };

        ReadPlaces.Weaker(reads).ShouldBe([0, 1]);
    }

    [Fact]
    public void A_read_that_loses_to_any_read_standing_in_a_place_takes_none_of_it()
    {
        var reads = new[] { Vision(0.30, score: 0.95), Vision(0.31, score: 0.80), Model(0.305, score: 0.90) };

        ReadPlaces.Weaker(reads).ShouldBe([2]);
    }

    [Fact]
    public void Nothing_to_compare_is_no_losers()
    {
        ReadPlaces.Weaker([]).ShouldBeEmpty();
        ReadPlaces.Weaker([Vision(0.10)]).ShouldBeEmpty();
    }

    // ---- one engine's own two reads of a place are not two engines disagreeing -----------------------------------------------

    [Fact]
    public void Two_Vision_reads_of_one_place_both_stand_because_an_engine_reading_a_spine_twice_is_a_repeat_not_a_contest()
    {
        ReadPlaces.Weaker([Vision(0.30, score: 0.95), Vision(0.31, ReadGrounding.Nothing, 0)]).ShouldBeEmpty();
    }

    [Fact]
    public void Two_model_reads_of_one_place_both_stand_for_the_same_reason()
    {
        ReadPlaces.Weaker([Model(0.30, score: 0.95), Model(0.31, ReadGrounding.Nothing, 0)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_wide_Vision_piece_holding_several_of_its_own_reads_is_no_merged_piece()
    {
        ReadPlaces.Weaker([Vision(0.10, width: 0.50), Vision(0.12), Vision(0.30), Vision(0.50)]).ShouldBeEmpty();
    }

    // ---- a piece covering several spines is not a place ---------------------------------------------------------------------

    [Fact]
    public void A_Vision_piece_covering_several_spines_loses_its_place_and_every_model_read_inside_it_stands()
    {
        ReadPlaces.Weaker([Vision(0.10, width: 0.50), Model(0.12), Model(0.30), Model(0.50)]).ShouldBe([0]);
    }

    [Fact]
    public void A_read_no_bigger_than_the_reads_around_it_is_no_piece_over_them_however_they_are_staggered()
    {
        // Four spine-sized reads, each engine's half a spine off the other's: a shelf read twice, not one piece over a run of
        // spines. Nothing grounds, so nothing contests either, and every read keeps its row.
        var reads = new[]
        {
            Model(0.00, ReadGrounding.Nothing, 0),
            Vision(0.02, ReadGrounding.Nothing, 0),
            Model(0.04, ReadGrounding.Nothing, 0),
            Vision(0.06, ReadGrounding.Nothing, 0),
        };

        ReadPlaces.Weaker(reads).ShouldBeEmpty();
    }

    [Fact]
    public void A_piece_covering_several_spines_loses_its_place_even_when_it_is_the_read_that_matched_and_a_read_inside_it_found_the_same_release()
    {
        var reads = new[]
        {
            Vision(0.10, ReadGrounding.Matched, 0.99, width: 0.50) with { Book = "piece" },
            Model(0.12, ReadGrounding.Matched, 0.9) with { Book = "piece" },
            Model(0.50, ReadGrounding.Nothing, 0),
        };

        ReadPlaces.Weaker(reads).ShouldBe([0]);
    }

    [Fact]
    public void One_spine_the_model_read_twice_inside_a_wide_piece_is_still_a_place_so_the_piece_is_contested_not_dropped()
    {
        ReadPlaces.Weaker([Vision(0.10, score: 0.90, width: 0.50), Model(0.12, score: 0.95), Model(0.13, score: 0.80)]).ShouldBe([0]);
    }

    [Fact]
    public void A_model_read_holding_two_Vision_pieces_loses_its_place_too_because_the_rule_reads_either_way()
    {
        ReadPlaces.Weaker([Vision(0.12), Vision(0.50), Model(0.10, width: 0.50)]).ShouldBe([2]);
    }

    // ---- a cross-engine read of the same release confirms the read that stands (#622) ---------------------------------------

    [Fact]
    public void A_model_read_that_takes_a_place_from_a_Visions_read_of_the_same_book_is_confirmed()
    {
        var reads = new[] { Vision(0.30, ReadGrounding.Ambiguous, 0.8) with { Book = "a" }, Model(0.31, score: 0.95) with { Book = "a" } };

        ReadPlaces.Confirmed(reads).ShouldBe([1]);
    }

    [Fact]
    public void A_Visions_read_that_takes_a_place_from_a_models_read_of_the_same_book_is_confirmed_too_though_it_was_never_unconfirmed()
    {
        var reads = new[] { Vision(0.30, score: 0.95) with { Book = "a" }, Model(0.31, ReadGrounding.Ambiguous, 0.8) with { Book = "a" } };

        ReadPlaces.Confirmed(reads).ShouldBe([0]); // harmless: the caller only clears Unconfirmed, which Vision's read never had
    }

    [Fact]
    public void A_model_read_that_takes_a_place_from_a_Visions_read_of_a_different_book_is_not_confirmed()
    {
        var reads = new[] { Vision(0.30, ReadGrounding.Ambiguous, 0.8) with { Book = "a" }, Model(0.31, score: 0.95) with { Book = "b" } };

        ReadPlaces.Confirmed(reads).ShouldBeEmpty();
    }

    [Fact]
    public void A_read_with_no_book_is_never_confirmed()
    {
        var reads = new[] { Vision(0.30, ReadGrounding.Ambiguous, 0.8), Model(0.31, score: 0.95) };

        ReadPlaces.Confirmed(reads).ShouldBeEmpty();
    }

    [Fact]
    public void Two_model_reads_of_one_place_are_not_confirmed_because_that_is_one_engines_own_repeat_not_agreement()
    {
        var reads = new[] { Model(0.30, ReadGrounding.Ambiguous, 0.8) with { Book = "a" }, Model(0.31, score: 0.95) with { Book = "a" } };

        ReadPlaces.Confirmed(reads).ShouldBeEmpty();
    }

    [Fact]
    public void A_merged_piece_never_confirms_any_of_the_spines_inside_it_even_when_it_names_one_of_their_books()
    {
        // The wide Vision piece happens to read the same book as the first spine inside it; it is still no place of its own, so
        // it is no confirmation either, the same as #622's example without boxes would have been before #620.
        var reads = new[]
        {
            Vision(0.10, width: 0.50) with { Book = "a" },
            Model(0.12) with { Book = "a" },
            Model(0.30) with { Book = "b" },
        };

        ReadPlaces.Confirmed(reads).ShouldBeEmpty();
    }

    [Fact]
    public void Reads_of_the_same_book_in_different_places_do_not_confirm_each_other()
    {
        var reads = new[] { Vision(0.10, ReadGrounding.Ambiguous, 0.8) with { Book = "a" }, Model(0.60, score: 0.95) with { Book = "a" } };

        ReadPlaces.Confirmed(reads).ShouldBeEmpty();
    }

    [Fact]
    public void Nothing_to_compare_confirms_nothing()
    {
        ReadPlaces.Confirmed([]).ShouldBeEmpty();
        ReadPlaces.Confirmed([Vision(0.10) with { Book = "a" }]).ShouldBeEmpty();
    }

    // ---- a merged piece with a release of its own stands aside; the read that holds the other's words wins its place ----

    [Fact]
    public void A_merged_Vision_piece_whose_release_none_of_the_model_reads_inside_it_found_stands_beside_them()
    {
        var reads = new[]
        {
            Vision(0.10, ReadGrounding.Ambiguous, 0.8, width: 0.50) with { Book = "piece" },
            Model(0.12, ReadGrounding.Nothing, 0),
            Model(0.40, ReadGrounding.Matched, 0.9) with { Book = "inner" },
        };

        ReadPlaces.Weaker(reads).ShouldBeEmpty();
    }

    [Fact]
    public void A_merged_piece_whose_release_an_inner_read_found_still_gives_way()
    {
        var reads = new[]
        {
            Vision(0.10, ReadGrounding.Ambiguous, 0.8, width: 0.50) with { Book = "piece" },
            Model(0.12, ReadGrounding.Matched, 0.9) with { Book = "piece" },
            Model(0.40, ReadGrounding.Matched, 0.9) with { Book = "inner" },
        };

        ReadPlaces.Weaker(reads).ShouldBe([0]);
    }

    [Fact]
    public void A_merged_piece_that_names_no_release_still_gives_way()
    {
        var reads = new[] { Vision(0.10, ReadGrounding.Nothing, 0, width: 0.50), Model(0.12), Model(0.40) };

        ReadPlaces.Weaker(reads).ShouldBe([0]);
    }

    [Fact]
    public void A_spine_found_by_a_line_still_contends_as_nothing_beside_a_piece_that_stands_aside()
    {
        var piece = Vision(0.10, ReadGrounding.Ambiguous, 0.8, width: 0.50) with { Book = "piece" };
        var revived = ReadPlaces.Of(FoundByLine(1.0), new ReadBox(0.12, 0.1, 0.05, 0.8), unconfirmed: true);

        ReadPlaces.Weaker([piece, revived, Model(0.40)]).ShouldBeEmpty();
    }

    [Fact]
    public void At_one_place_the_read_whose_words_and_release_hold_the_others_words_is_shown_though_it_scores_lower()
    {
        var cutShort = Vision(0.30, score: 1.0) with { ReadText = "Iron Fox", BestTitle = "Iron Fox" };
        var whole = Model(0.31, score: 0.67) with { ReadText = "Iron Fox Rising", BestTitle = "Iron Fox Rising" };

        ReadPlaces.Weaker([cutShort, whole]).ShouldBe([0]);
        ReadPlaces.Weaker([whole with { Unconfirmed = false }, cutShort with { Unconfirmed = true }]).ShouldBe([1]);
    }

    [Fact]
    public void The_longer_read_does_not_win_when_its_release_does_not_hold_the_extra_words()
    {
        var cutShort = Vision(0.30, score: 1.0) with { ReadText = "Iron Fox", BestTitle = "Iron Fox" };
        var longer = Model(0.31, score: 0.67) with { ReadText = "Iron Fox Rising", BestTitle = "Iron Fox" };

        ReadPlaces.Weaker([cutShort, longer]).ShouldBe([1]);
    }

    [Fact]
    public void Reads_with_no_containment_of_words_are_settled_by_score_as_before()
    {
        var first = Vision(0.30, score: 0.80) with { ReadText = "Iron Fox", BestTitle = "Iron Fox" };
        var second = Model(0.31, score: 0.95) with { ReadText = "Quiet Harbor", BestTitle = "Quiet Harbor" };

        ReadPlaces.Weaker([first, second]).ShouldBe([0]);
    }

    [Fact]
    public void A_merged_piece_that_found_nothing_still_gives_way_though_it_carries_a_release_key()
    {
        var reads = new[] { Vision(0.10, ReadGrounding.Nothing, 0, width: 0.50) with { Book = "piece" }, Model(0.12), Model(0.40) };

        ReadPlaces.Weaker(reads).ShouldBe([0]);
    }

    [Fact]
    public void A_merged_piece_whose_release_a_read_of_the_other_engine_found_elsewhere_gives_way_so_the_release_is_not_shown_twice()
    {
        var reads = new[]
        {
            Vision(0.10, ReadGrounding.Ambiguous, 0.8, width: 0.50) with { Book = "piece" },
            Model(0.12, ReadGrounding.Nothing, 0),
            Model(0.40, ReadGrounding.Nothing, 0),
            Model(0.80, ReadGrounding.Matched, 0.9) with { Book = "piece" },
        };

        ReadPlaces.Weaker(reads).ShouldBe([0]);
    }

    [Fact]
    public void A_read_of_the_pieces_own_engine_naming_its_release_does_not_make_the_piece_give_way()
    {
        var reads = new[]
        {
            Vision(0.10, ReadGrounding.Ambiguous, 0.8, width: 0.50) with { Book = "piece" },
            Model(0.12, ReadGrounding.Nothing, 0),
            Model(0.40, ReadGrounding.Nothing, 0),
            Vision(0.80, ReadGrounding.Matched, 0.9) with { Book = "piece" },
        };

        ReadPlaces.Weaker(reads).ShouldBeEmpty();
    }

    [Fact]
    public void A_read_with_a_neighbours_words_bled_in_does_not_beat_the_right_shorter_read()
    {
        var right = Model(0.30, score: 1.0) with { ReadText = "Iron Fox", BestTitle = "Iron Fox" };
        var bled = Vision(0.31, score: 0.70) with { ReadText = "Iron Fox Quiet Harbor", BestTitle = "Quiet Harbor" };

        ReadPlaces.Weaker([bled, right]).ShouldBe([0]);
        ReadPlaces.Weaker([right with { Unconfirmed = false }, bled with { Unconfirmed = true }]).ShouldBe([1]);
    }

    [Fact]
    public void A_longer_read_of_a_lower_grade_does_not_beat_the_matched_shorter_one()
    {
        var shorter = Vision(0.30, ReadGrounding.Matched, 1.0) with { ReadText = "Iron Fox", BestTitle = "Iron Fox" };
        var longer = Model(0.31, ReadGrounding.Ambiguous, 0.67) with { ReadText = "Iron Fox Rising", BestTitle = "Iron Fox Rising" };

        ReadPlaces.Weaker([shorter, longer]).ShouldBe([1]);
        ReadPlaces.Weaker([longer with { Unconfirmed = false }, shorter with { Unconfirmed = true }]).ShouldBe([0]);
    }

    [Fact]
    public void A_read_whose_words_do_not_hold_the_others_is_settled_by_score_though_its_release_holds_them()
    {
        var first = Vision(0.30, score: 0.80) with { ReadText = "Iron Fox", BestTitle = "Fox Tales" };
        var second = Model(0.31, score: 0.60) with { ReadText = "Quiet Harbor", BestTitle = "Quiet Harbor" };

        ReadPlaces.Weaker([first, second]).ShouldBe([1]);
    }

    [Fact]
    public void Two_reads_of_the_same_words_are_settled_by_score_whichever_has_a_best_title()
    {
        var first = Vision(0.30, score: 0.80) with { ReadText = "Iron Fox", BestTitle = "Iron Fox" };
        var second = Model(0.31, score: 0.95) with { ReadText = "Fox Iron", BestTitle = null };

        ReadPlaces.Weaker([first, second]).ShouldBe([0]);
    }

    [Fact]
    public void A_read_of_stop_words_only_is_settled_by_score_against_a_read_with_words()
    {
        var first = Vision(0.30, score: 0.80) with { ReadText = "Iron Fox", BestTitle = "Iron Fox" };
        var second = Model(0.31, score: 0.95) with { ReadText = "The", BestTitle = "The Thing" };

        ReadPlaces.Weaker([first, second]).ShouldBe([0]);
    }

    [Fact]
    public void A_longer_read_whose_release_misses_one_of_the_extra_words_does_not_beat_the_shorter_read()
    {
        var shorter = Vision(0.30, score: 1.0) with { ReadText = "Iron Fox", BestTitle = "Iron Fox" };
        var longer = Model(0.31, score: 0.67) with { ReadText = "Iron Fox Rising Again", BestTitle = "Iron Fox Rising" };

        ReadPlaces.Weaker([shorter, longer]).ShouldBe([1]);
    }

    [Fact]
    public void A_longer_read_with_misread_words_wins_its_place_when_its_release_holds_the_shorter_reads_words_and_two_of_its_own()
    {
        var longer = Vision(0.30, ReadGrounding.Ambiguous, 0.784)
            with
        { ReadText = "Darby O'Gill Alf Wiskeys Little People", BestTitle = "Darby O'Gill and the Little People" };
        var shorter = Model(0.31, ReadGrounding.Ambiguous, 1.0) with { ReadText = "Little People", BestTitle = "The Little People" };

        ReadPlaces.Weaker([longer, shorter]).ShouldBe([1]);
        ReadPlaces.Weaker([shorter with { Unconfirmed = false }, longer with { Unconfirmed = true }]).ShouldBe([0]);
    }

    [Fact]
    public void A_longer_read_whose_release_holds_only_one_of_its_extra_words_does_not_win_on_misread_words()
    {
        var longer = Vision(0.30, ReadGrounding.Ambiguous, 0.784)
            with
        { ReadText = "Darby O'Gill Alf Wiskeys Little People", BestTitle = "Darby and the Little People" };
        var shorter = Model(0.31, ReadGrounding.Ambiguous, 1.0) with { ReadText = "Little People", BestTitle = "The Little People" };

        ReadPlaces.Weaker([longer, shorter]).ShouldBe([0]);
    }

    [Fact]
    public void A_longer_read_whose_release_lacks_one_of_the_shorter_reads_words_does_not_win_however_many_extra_words_it_holds()
    {
        var longer = Vision(0.30, ReadGrounding.Ambiguous, 0.784)
            with
        { ReadText = "Darby O'Gill Alf Wiskeys Little People", BestTitle = "Darby O'Gill and the People" };
        var shorter = Model(0.31, ReadGrounding.Ambiguous, 1.0) with { ReadText = "Little People", BestTitle = "The Little People" };

        ReadPlaces.Weaker([longer, shorter]).ShouldBe([0]);
    }

    [Fact]
    public void ReadPlaces_Of_carries_the_reads_words_and_its_best_title()
    {
        var answer = new BarcodeResolution(
            ResolutionVerdict.Matched, [new ResolutionOption(null, TrimmedRetryHarness.Known("Iron Fox Rising"), 0.67)], null, []);

        var placed = ReadPlaces.Of(answer, new ReadBox(0.30, 0.1, 0.05, 0.8), unconfirmed: true, readText: "Iron Fox Rising", bestTitle: "Iron Fox Rising");

        placed.ReadText.ShouldBe("Iron Fox Rising");
        placed.BestTitle.ShouldBe("Iron Fox Rising");
    }
}
