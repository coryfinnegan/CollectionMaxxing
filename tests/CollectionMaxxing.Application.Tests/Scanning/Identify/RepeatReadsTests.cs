using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// One book, one candidate per photo: a spine the engine read twice (once clean, once with OCR junk on the end) points at
// one release both times, and only the strongest of those reads stands. Reads carry the box they sit in, so the same release
// read in two different places (a shelf of copies, or a series of tapes with one title on every spine) is two items.
// Neutral titles throughout.
public class RepeatReadsTests
{
    private static readonly Guid BookA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BookB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static BarcodeResolution Local(ResolutionVerdict verdict, Guid release, double score) =>
        new(verdict, [new ResolutionOption(release, null, score)], null, []);

    private static BarcodeResolution Provided(ResolutionVerdict verdict, string provider, string externalId, double score) =>
        new(verdict, [new ResolutionOption(null, new ProviderRelease { Provider = provider, ExternalId = externalId, Title = "A Title", Kind = WorkKind.Book }, score)], null, []);

    [Fact]
    public void A_matched_read_beats_an_ambiguous_repeat_of_the_same_release_even_when_the_repeat_came_first()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.95), Local(ResolutionVerdict.Matched, BookA, 0.92) };

        RepeatReads.Of(answers).ShouldBe([0]);
    }

    [Fact]
    public void A_matched_read_keeps_its_place_against_a_later_ambiguous_repeat()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.92), Local(ResolutionVerdict.Ambiguous, BookA, 0.7) };

        RepeatReads.Of(answers).ShouldBe([1]);
    }

    [Fact]
    public void Between_two_ambiguous_reads_of_one_release_the_higher_score_stands()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.6), Local(ResolutionVerdict.Ambiguous, BookA, 0.7) };

        RepeatReads.Of(answers).ShouldBe([0]);
    }

    [Fact]
    public void Between_two_matched_reads_of_one_release_the_higher_score_stands()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.91), Local(ResolutionVerdict.Matched, BookA, 0.99) };

        RepeatReads.Of(answers).ShouldBe([0]);
    }

    [Fact]
    public void A_tie_keeps_the_earlier_read()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.Of(answers).ShouldBe([1]);
    }

    [Fact]
    public void Three_reads_of_one_release_leave_exactly_one()
    {
        var answers = new[]
        {
            Local(ResolutionVerdict.Ambiguous, BookA, 0.8),
            Local(ResolutionVerdict.Matched, BookA, 0.93),
            Local(ResolutionVerdict.Matched, BookA, 0.97),
        };

        RepeatReads.Of(answers).ShouldBe([0, 1]);
    }

    [Fact]
    public void Reads_whose_best_options_are_different_releases_stay_apart()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Matched, BookB, 0.95) };

        RepeatReads.Of(answers).ShouldBeEmpty();
    }

    [Fact]
    public void Two_reads_that_only_share_a_runner_up_stay_apart()
    {
        var first = new BarcodeResolution(
            ResolutionVerdict.Ambiguous, [new ResolutionOption(BookA, null, 0.8), new ResolutionOption(BookB, null, 0.78)], null, []);
        var second = new BarcodeResolution(
            ResolutionVerdict.Ambiguous, [new ResolutionOption(BookB, null, 0.82), new ResolutionOption(BookA, null, 0.7)], null, []);

        RepeatReads.Of([first, second]).ShouldBeEmpty();
    }

    [Fact]
    public void The_same_provider_and_external_id_is_one_release()
    {
        var answers = new[]
        {
            Provided(ResolutionVerdict.Ambiguous, "openlibrary", "OL1W", 0.7),
            Provided(ResolutionVerdict.Matched, "openlibrary", "OL1W", 0.95),
        };

        RepeatReads.Of(answers).ShouldBe([0]);
    }

    [Fact]
    public void The_same_external_id_at_another_provider_is_another_release()
    {
        var answers = new[]
        {
            Provided(ResolutionVerdict.Matched, "openlibrary", "1", 0.95),
            Provided(ResolutionVerdict.Matched, "googlebooks", "1", 0.95),
        };

        RepeatReads.Of(answers).ShouldBeEmpty();
    }

    [Fact]
    public void A_catalogue_release_and_a_provider_release_are_not_the_same_release()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Provided(ResolutionVerdict.Matched, "openlibrary", "OL1W", 0.95) };

        RepeatReads.Of(answers).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ResolutionVerdict.NotFound)]
    [InlineData(ResolutionVerdict.Pending)]
    public void A_read_with_no_answer_is_never_a_repeat_and_never_makes_one(ResolutionVerdict verdict)
    {
        var answers = new[]
        {
            new BarcodeResolution(verdict, [], null, []),
            Local(ResolutionVerdict.Matched, BookA, 0.95),
            new BarcodeResolution(verdict, [], null, []),
        };

        RepeatReads.Of(answers).ShouldBeEmpty();
    }

    [Fact]
    public void Unrelated_reads_in_between_do_not_hide_a_repeat()
    {
        var answers = new[]
        {
            Local(ResolutionVerdict.Matched, BookA, 0.95),
            Local(ResolutionVerdict.Matched, BookB, 0.95),
            Local(ResolutionVerdict.Ambiguous, BookA, 0.75),
        };

        RepeatReads.Of(answers).ShouldBe([2]);
    }

    [Fact]
    public void Nothing_to_compare_is_no_repeats()
    {
        RepeatReads.Of([]).ShouldBeEmpty();
    }

    // ---- position: the same release in two places is two items --------------------------------------------------------------

    private static ReadBox Spine(double x, double width = 0.05) => new(x, 0.1, width, 0.8);

    [Fact]
    public void Two_reads_of_one_release_in_the_same_place_are_a_repeat()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.7), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.Of(answers, [Spine(0.30), Spine(0.31)]).ShouldBe([0]);
    }

    [Fact]
    public void Two_reads_of_one_release_in_separate_places_are_two_items_and_both_are_kept()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.Of(answers, [Spine(0.10), Spine(0.60)]).ShouldBeEmpty();
    }

    [Fact]
    public void Spines_that_only_touch_edge_to_edge_are_separate_places()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.Of(answers, [Spine(0.10), Spine(0.15)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_small_box_inside_a_big_one_is_the_same_place_because_the_smaller_box_is_what_counts()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Matched, BookA, 0.90) };

        RepeatReads.Of(answers, [new ReadBox(0.1, 0.1, 0.5, 0.8), new ReadBox(0.2, 0.2, 0.1, 0.3)]).ShouldBe([1]);
    }

    [Fact]
    public void Boxes_that_overlap_by_less_than_half_of_the_smaller_one_are_separate_places()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.Of(answers, [new ReadBox(0.10, 0.1, 0.10, 0.8), new ReadBox(0.17, 0.1, 0.10, 0.8)]).ShouldBeEmpty();
        RepeatReads.Of(answers, [new ReadBox(0.10, 0.1, 0.10, 0.8), new ReadBox(0.15, 0.1, 0.10, 0.8)]).ShouldBe([1]);
    }

    [Fact]
    public void A_read_with_no_box_is_compared_the_way_reads_always_were_so_it_is_a_repeat_of_the_same_release()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Ambiguous, BookA, 0.7) };

        RepeatReads.Of(answers, [Spine(0.10), null]).ShouldBe([1]);
        RepeatReads.Of(answers, [null, Spine(0.60)]).ShouldBe([1]);
        RepeatReads.Of(answers, [null, null]).ShouldBe([1]);
        RepeatReads.Of(answers).ShouldBe([1]);
    }

    [Fact]
    public void The_stronger_of_two_reads_in_one_place_stands_and_a_read_elsewhere_stands_too()
    {
        var answers = new[]
        {
            Local(ResolutionVerdict.Ambiguous, BookA, 0.7),
            Local(ResolutionVerdict.Matched, BookA, 0.95),
            Local(ResolutionVerdict.Matched, BookA, 0.92),
        };

        RepeatReads.Of(answers, [Spine(0.10), Spine(0.11), Spine(0.60)]).ShouldBe([0]);
    }

    [Fact]
    public void A_stronger_read_in_the_second_place_does_not_displace_the_read_in_the_first()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.6), Local(ResolutionVerdict.Matched, BookA, 0.99) };

        RepeatReads.Of(answers, [Spine(0.10), Spine(0.60)]).ShouldBeEmpty();
    }

    [Fact]
    public void Positions_do_not_make_reads_of_different_releases_repeats_even_in_the_same_place()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Matched, BookB, 0.95) };

        RepeatReads.Of(answers, [Spine(0.10), Spine(0.10)]).ShouldBeEmpty();
    }

    // ---- a cross-engine repeat of the same release confirms the read that stands (#622) ---------------------------------------

    [Fact]
    public void A_model_read_that_beats_a_Visions_read_of_the_same_book_in_the_same_place_is_confirmed()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.7), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.ConfirmedByOtherEngine(answers, [false, true], [Spine(0.30), Spine(0.31)]).ShouldBe([1]);
    }

    [Fact]
    public void A_Visions_read_that_beats_a_models_read_of_the_same_book_is_confirmed_too_though_it_was_never_unconfirmed()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookA, 0.95), Local(ResolutionVerdict.Ambiguous, BookA, 0.7) };

        RepeatReads.ConfirmedByOtherEngine(answers, [false, true], [Spine(0.30), Spine(0.31)]).ShouldBe([0]); // harmless: the caller only clears Unconfirmed, which Vision's read never had
    }

    [Fact]
    public void Two_model_reads_of_one_place_are_not_confirmed_because_that_is_one_engines_own_repeat_not_agreement()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.7), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.ConfirmedByOtherEngine(answers, [true, true], [Spine(0.30), Spine(0.31)]).ShouldBeEmpty();
    }

    [Fact]
    public void Two_Vision_reads_of_one_place_are_not_confirmed_either_since_neither_is_model_only()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.7), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.ConfirmedByOtherEngine(answers, [false, false], [Spine(0.30), Spine(0.31)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_model_read_that_beats_a_Visions_read_of_a_different_book_in_the_same_place_is_not_this_rules_business()
    {
        var answers = new[] { Local(ResolutionVerdict.Matched, BookB, 0.7), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.ConfirmedByOtherEngine(answers, [false, true], [Spine(0.30), Spine(0.31)]).ShouldBeEmpty();
    }

    [Fact]
    public void A_model_read_that_beats_a_Visions_read_of_the_same_book_in_another_place_is_not_confirmed()
    {
        var answers = new[] { Local(ResolutionVerdict.Ambiguous, BookA, 0.7), Local(ResolutionVerdict.Matched, BookA, 0.95) };

        RepeatReads.ConfirmedByOtherEngine(answers, [false, true], [Spine(0.10), Spine(0.60)]).ShouldBeEmpty();
    }

    [Fact]
    public void Three_reads_of_one_release_in_one_place_confirm_the_survivor_when_any_of_them_crossed_engines()
    {
        var answers = new[]
        {
            Local(ResolutionVerdict.Ambiguous, BookA, 0.8),
            Local(ResolutionVerdict.Matched, BookA, 0.93),
            Local(ResolutionVerdict.Matched, BookA, 0.97),
        };

        // Vision (false), model (true), model (true): the slot saw both engines before the final model read took it.
        RepeatReads.ConfirmedByOtherEngine(answers, [false, true, true]).ShouldBe([2]);
    }

    [Fact]
    public void Nothing_to_compare_confirms_nothing()
    {
        RepeatReads.ConfirmedByOtherEngine([], []).ShouldBeEmpty();
        RepeatReads.ConfirmedByOtherEngine([Local(ResolutionVerdict.Matched, BookA, 0.95)], [true]).ShouldBeEmpty();
    }

    [Fact]
    public void A_chain_of_three_reads_each_overlapping_only_its_neighbour_does_not_lend_the_first_reads_engine_to_the_third()
    {
        // Vision at [0, .05] and model1 at [.025, .075] share a place; model1 and model2 at [.05, .10] share a place; Vision
        // and model2 do not overlap at all. Same place is not transitive: model2 winning the chain must not borrow Vision's
        // agreement with model1, because Vision never read where model2 sits.
        var answers = new[]
        {
            Local(ResolutionVerdict.Ambiguous, BookA, 0.50),
            Local(ResolutionVerdict.Matched, BookA, 0.90),
            Local(ResolutionVerdict.Matched, BookA, 0.95),
        };
        var boxes = new ReadBox?[] { Spine(0.00), Spine(0.025), Spine(0.05) };

        RepeatReads.ConfirmedByOtherEngine(answers, [false, true, true], boxes).ShouldBeEmpty();
    }
}
