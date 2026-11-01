using CollectionMaxxing.Application.Scanning.Identify;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// One item per place. Two engines read the same shelf and often read the same spine: one place holds one thing, whatever each
// read made of it, so the read that grounds better is the row shown and the other is skipped. A piece covering several spines
// (Vision sometimes makes one of a run of them) is not a place: the reads inside it are the spines, and each keeps its own.
public class ReadPlacesTests
{
    /// <summary>A spine <paramref name="width"/> wide whose left edge is at <paramref name="x"/>.</summary>
    private static PlacedRead At(double x, ReadGrounding grounding = ReadGrounding.Matched, double score = 0.9, double width = 0.05) =>
        new(new ReadBox(x, 0.1, width, 0.8), grounding, score);

    private static PlacedRead Nowhere(ReadGrounding grounding = ReadGrounding.Matched, double score = 0.9) => new(null, grounding, score);

    [Fact]
    public void A_read_that_matched_takes_the_place_from_one_that_only_narrowed_it_down()
    {
        ReadPlaces.Weaker([At(0.30, ReadGrounding.Ambiguous, 0.95), At(0.31, ReadGrounding.Matched, 0.80)]).ShouldBe([0]);
    }

    [Fact]
    public void A_matched_read_keeps_its_place_against_a_later_ambiguous_one()
    {
        ReadPlaces.Weaker([At(0.30, ReadGrounding.Matched, 0.80), At(0.31, ReadGrounding.Ambiguous, 0.95)]).ShouldBe([1]);
    }

    [Fact]
    public void Between_two_matched_reads_of_one_place_the_higher_score_stands()
    {
        ReadPlaces.Weaker([At(0.30, score: 0.80), At(0.31, score: 0.95)]).ShouldBe([0]);
        ReadPlaces.Weaker([At(0.30, score: 0.95), At(0.31, score: 0.80)]).ShouldBe([1]);
    }

    [Fact]
    public void A_tie_keeps_the_earlier_read_so_Visions_own_stands_where_the_model_read_no_better()
    {
        ReadPlaces.Weaker([At(0.30, score: 0.90), At(0.31, score: 0.90)]).ShouldBe([1]);
    }

    [Fact]
    public void A_read_that_found_nothing_loses_its_place_to_one_that_matched_whichever_came_first()
    {
        ReadPlaces.Weaker([At(0.30, ReadGrounding.Nothing, 0), At(0.31, ReadGrounding.Matched)]).ShouldBe([0]);
        ReadPlaces.Weaker([At(0.30, ReadGrounding.Matched), At(0.31, ReadGrounding.Nothing, 0)]).ShouldBe([1]);
    }

    [Fact]
    public void Two_reads_that_both_found_nothing_are_left_alone_because_there_is_no_better_read_to_pick()
    {
        ReadPlaces.Weaker([At(0.30, ReadGrounding.Nothing, 0), At(0.31, ReadGrounding.Nothing, 0)]).ShouldBeEmpty();
    }

    [Fact]
    public void Reads_in_separate_places_both_stand_however_they_read()
    {
        ReadPlaces.Weaker([At(0.10, ReadGrounding.Nothing, 0), At(0.60, ReadGrounding.Matched)]).ShouldBeEmpty();
    }

    [Fact]
    public void Spines_that_only_touch_edge_to_edge_are_separate_places()
    {
        ReadPlaces.Weaker([At(0.10), At(0.15)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_read_with_no_box_has_no_place_so_it_neither_takes_one_nor_loses_one()
    {
        ReadPlaces.Weaker([Nowhere(ReadGrounding.Nothing, 0), At(0.30)]).ShouldBeEmpty();
        ReadPlaces.Weaker([At(0.30), Nowhere(ReadGrounding.Matched, 0.99)]).ShouldBeEmpty();
        ReadPlaces.Weaker([Nowhere(), Nowhere()]).ShouldBeEmpty();
    }

    [Fact]
    public void A_read_still_being_worked_out_keeps_its_place_and_takes_none()
    {
        ReadPlaces.Weaker([At(0.30, ReadGrounding.Unsettled, 0), At(0.31, ReadGrounding.Matched)]).ShouldBeEmpty();
        ReadPlaces.Weaker([At(0.30, ReadGrounding.Matched), At(0.31, ReadGrounding.Unsettled, 0)]).ShouldBeEmpty();
    }

    [Fact]
    public void Three_reads_of_one_place_leave_the_strongest()
    {
        ReadPlaces.Weaker([At(0.30, ReadGrounding.Ambiguous, 0.8), At(0.31, score: 0.93), At(0.32, score: 0.97)]).ShouldBe([0, 1]);
    }

    [Fact]
    public void The_stronger_of_two_reads_in_one_place_stands_and_a_read_elsewhere_stands_too()
    {
        ReadPlaces.Weaker([At(0.10, ReadGrounding.Ambiguous, 0.7), At(0.11, ReadGrounding.Matched), At(0.60)]).ShouldBe([0]);
    }

    // ---- a piece covering several spines is not a place ---------------------------------------------------------------------

    [Fact]
    public void A_piece_covering_several_spines_loses_its_place_and_every_read_inside_it_stands()
    {
        var reads = new[] { At(0.10, width: 0.50), At(0.12), At(0.30), At(0.50) };

        ReadPlaces.Weaker(reads).ShouldBe([0]);
    }

    [Fact]
    public void A_piece_covering_several_spines_loses_its_place_even_when_it_is_the_read_that_matched()
    {
        var reads = new[]
        {
            At(0.10, ReadGrounding.Matched, 0.99, width: 0.50),
            At(0.12, ReadGrounding.Nothing, 0),
            At(0.50, ReadGrounding.Nothing, 0),
        };

        ReadPlaces.Weaker(reads).ShouldBe([0]);
    }

    [Fact]
    public void One_spine_read_twice_inside_a_wide_piece_is_still_a_place_so_the_strongest_read_stands()
    {
        var reads = new[] { At(0.10, score: 0.90, width: 0.50), At(0.12, score: 0.95), At(0.13, score: 0.80) };

        ReadPlaces.Weaker(reads).ShouldBe([0, 2]);
    }

    [Fact]
    public void A_middle_spine_that_overlaps_both_of_its_neighbours_loses_its_place_and_the_two_ends_keep_theirs()
    {
        var reads = new[] { At(0.10, width: 0.10), At(0.15, width: 0.10), At(0.20, width: 0.10) };

        ReadPlaces.Weaker(reads).ShouldBe([1]);
    }

    [Fact]
    public void Nothing_to_compare_is_no_losers()
    {
        ReadPlaces.Weaker([]).ShouldBeEmpty();
        ReadPlaces.Weaker([At(0.10)]).ShouldBeEmpty();
    }
}
