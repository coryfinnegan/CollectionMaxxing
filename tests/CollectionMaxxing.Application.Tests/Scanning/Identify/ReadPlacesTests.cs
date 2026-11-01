using CollectionMaxxing.Application.Scanning.Identify;

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
    public void A_piece_covering_several_spines_loses_its_place_even_when_it_is_the_read_that_matched()
    {
        var reads = new[]
        {
            Vision(0.10, ReadGrounding.Matched, 0.99, width: 0.50),
            Model(0.12, ReadGrounding.Nothing, 0),
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
}
