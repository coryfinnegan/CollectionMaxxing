using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The scoring maths on a tiny synthetic golden set generated in the test (a shelf of four known games and single-item pictures).
/// The numbers are exact: percentages are 0 to 100, rounded to two places.
/// </summary>
public sealed class ScoringMathsTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private ScoredEngines ScoreShelf(params GroundedItem[] read)
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64"), ShelfItem("Banjo-Kazooie"), ShelfItem("Goldeneye 007")]);
        return EvalScorer.Score([Photo("s1", Apple, read)], TruthsOf(_golden));
    }

    private static GroundedItem[] All4() =>
        [Matched("Star Fox 64"), Matched("Mario Kart 64"), Matched("Banjo-Kazooie"), Matched("Goldeneye 007")];

    [Fact]
    public void A_perfect_reading_scores_everything_at_100_with_no_invented_titles()
    {
        var shelf = ScoreShelf(All4()).ShelfOf();

        shelf.TruthItems.ShouldBe(4);
        shelf.PredictedItems.ShouldBe(4);
        shelf.RecallPct.ShouldBe(100);
        shelf.PrecisionPct.ShouldBe(100);
        shelf.Top1Pct.ShouldBe(100);
        shelf.Top3Pct.ShouldBe(100);
        shelf.TitleAccuracyPct.ShouldBe(100);
        shelf.PlatformAccuracyPct.ShouldBe(100);
        shelf.TypeAccuracyPct.ShouldBe(100);
        shelf.InventedTitles.ShouldBe(0);
        shelf.InventedPct.ShouldBe(0);
    }

    [Fact]
    public void One_missed_item_costs_a_quarter_of_recall_and_nothing_of_precision()
    {
        var shelf = ScoreShelf(All4()[..3]).ShelfOf();

        shelf.RecallPct.ShouldBe(75);
        shelf.PrecisionPct.ShouldBe(100);
        shelf.Top1Pct.ShouldBe(75);
        shelf.Top3Pct.ShouldBe(75);
        shelf.InventedTitles.ShouldBe(0);
    }

    [Fact]
    public void One_extra_item_that_found_nothing_costs_precision_but_is_not_an_invented_title()
    {
        var shelf = ScoreShelf([.. All4(), NotFound("some words on a spine")]).ShelfOf();

        shelf.PredictedItems.ShouldBe(5);
        shelf.RecallPct.ShouldBe(100);
        shelf.PrecisionPct.ShouldBe(80);
        shelf.InventedTitles.ShouldBe(0);
    }

    [Fact]
    public void One_invented_title_that_survives_grounding_is_counted_and_costs_precision()
    {
        var shelf = ScoreShelf([.. All4()[..3], Matched("Zorblax Quest", readAs: "Goldeneye 007")]).ShelfOf();

        shelf.PredictedItems.ShouldBe(4);
        shelf.InventedTitles.ShouldBe(1);
        shelf.InventedPct.ShouldBe(25);
        shelf.PrecisionPct.ShouldBe(75);
        shelf.RecallPct.ShouldBe(75);
    }

    [Fact]
    public void A_wrong_best_release_with_the_right_one_second_is_a_top_1_miss_and_a_top_3_hit()
    {
        var ambiguous = Ambiguous(Release("Mario Kart 64 Special"), Release("Mario Kart 64"), Release("Mario Kart Wii", "Wii"));

        var shelf = ScoreShelf(Matched("Star Fox 64"), ambiguous, Matched("Banjo-Kazooie"), Matched("Goldeneye 007")).ShelfOf();

        shelf.Top1Pct.ShouldBe(75);
        shelf.Top3Pct.ShouldBe(100);
        shelf.RecallPct.ShouldBe(75);
        shelf.PrecisionPct.ShouldBe(75);
    }

    [Fact]
    public void A_title_on_the_right_platform_only_counts_when_the_platform_agrees()
    {
        var shelf = ScoreShelf(
            Matched("Star Fox 64"),
            Matched("Mario Kart 64", platform: "Wii"), // the right title on the wrong console
            Matched("Banjo-Kazooie"),
            Matched("Goldeneye 007")).ShelfOf();

        shelf.Top1Pct.ShouldBe(75);
        shelf.TitleAccuracyPct.ShouldBe(100);
        shelf.PlatformAccuracyPct.ShouldBe(75);
        shelf.InventedTitles.ShouldBe(0); // a real title on the wrong console is a platform error, not an invention
    }

    [Fact]
    public void Titles_match_the_way_the_live_scorer_matches_them_and_platform_names_by_alias()
    {
        _golden.AddShelf("s1", [ShelfItem("The Legend of Zelda", platform: "N64")]);

        var scored = EvalScorer.Score(
            [Photo("s1", Apple, [Matched("Legend of Zelda, The", platform: "Nintendo 64")])], TruthsOf(_golden));

        scored.ShelfOf().Top1Pct.ShouldBe(100);
    }

    [Fact]
    public void A_truth_item_is_claimed_once_so_a_repeated_read_counts_against_precision()
    {
        var shelf = ScoreShelf(Matched("Star Fox 64"), Matched("Star Fox 64"), Matched("Mario Kart 64")).ShelfOf();

        shelf.Top1Pct.ShouldBe(50);
        shelf.CorrectItems.ShouldBe(2);
        shelf.PrecisionPct.ShouldBe(66.67);
        shelf.InventedTitles.ShouldBe(0); // a second copy of a real title is not invented
    }

    [Fact]
    public void A_refused_photo_misses_every_item_and_counts_as_a_refusal()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64")]);
        _golden.AddShelf("s2", [ShelfItem("Banjo-Kazooie"), ShelfItem("Goldeneye 007")]);

        var scored = EvalScorer.Score(
            [Photo("s1", Apple, [], outcome: "refused"), Photo("s2", Apple, [Matched("Banjo-Kazooie"), Matched("Goldeneye 007")])],
            TruthsOf(_golden));

        var shelf = scored.ShelfOf();
        shelf.Refusals.ShouldBe(1);
        shelf.RefusalPct.ShouldBe(50);
        shelf.RecallPct.ShouldBe(50);
        shelf.PrecisionPct.ShouldBe(100);
        scored.Misses.Count(m => m.Kind == "refused").ShouldBe(2);
    }

    [Fact]
    public void Latency_percentiles_are_nearest_rank_and_only_photos_that_answered_count()
    {
        for (var n = 1; n <= 10; n++)
        {
            _golden.AddShelf($"s{n}", [ShelfItem("Star Fox 64")]);
        }

        var photos = Enumerable.Range(1, 10).Select(n => Photo($"s{n}", Apple, [Matched("Star Fox 64")], latencyMs: n * 100, firstItemMs: n * 10)).ToList();
        photos.Add(Photo("s1", Vision, [], latencyMs: 99999, outcome: "error")); // a failure's latency is not a latency
        var shelf = EvalScorer.Score(photos, TruthsOf(_golden)).ShelfOf();

        shelf.LatencyP50Ms.ShouldBe(500);
        shelf.LatencyP95Ms.ShouldBe(1000);
        shelf.FirstItemP50Ms.ShouldBe(50);
        shelf.FirstItemP95Ms.ShouldBe(100);
    }

    [Fact]
    public void Engines_are_scored_apart_and_the_phone_ends_with_the_last_thermal_state()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64")]);

        var scored = EvalScorer.Score(
            [
                Photo("s1", Apple, [Matched("Star Fox 64"), Matched("Mario Kart 64")], thermal: "fair"),
                Photo("s1", Vision, [Matched("Star Fox 64")], thermal: "serious"),
            ],
            TruthsOf(_golden));

        scored.ShelfOf(Apple).RecallPct.ShouldBe(100);
        scored.ShelfOf(Vision).RecallPct.ShouldBe(50);
        scored.Engines[Apple].ThermalAtEnd.ShouldBe("fair");
        scored.Engines[Vision].ThermalAtEnd.ShouldBe("serious");
    }

    [Fact]
    public void A_single_picture_scores_top_1_top_3_platform_type_completeness_and_the_step_that_resolved_it_per_kind()
    {
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", EvalKinds.LooseCartridge, "Nintendo 64"));
        _golden.AddSingle("c2", new SyntheticGolden.SingleItem("The Matrix", "vhs", "complete", EvalKinds.VhsSleeve));
        _golden.AddSingle("c3", new SyntheticGolden.SingleItem("Pilotwings 64", "game_cartridge", "cart_only", EvalKinds.LooseCartridge, "Nintendo 64"));

        var photos = new[]
        {
            Photo("c1", Apple, [Matched("Star Fox 64", completeness: "cart_only")]),
            Photo("c2", Apple, [new GroundedItem("matrx", "box_only", "artwork", "ambiguous",
                [Release("The Matrix", null, "film", "dvd"), Release("The Matrix", null, "film", "vhs")])]),
            Photo("c3", Apple, [Matched("Wave Race 64", completeness: "cart_only")]),
        };
        var single = EvalScorer.Score(photos, TruthsOf(_golden)).SoloOf();

        single.TruthItems.ShouldBe(3);
        single.Top1Pct.ShouldBe(33.33); // only Star Fox
        single.Top3Pct.ShouldBe(66.67); // plus the VHS in second place
        single.CompletenessAccuracyPct.ShouldBe(66.67); // the matrix photo said box_only against complete
        single.PlatformAccuracyPct.ShouldBe(100); // Star Fox 64 and Wave Race 64 are not title matches; of the title matches with a platform, 1 of 1
        single.ByKind[EvalKinds.LooseCartridge].Items.ShouldBe(2);
        single.ByKind[EvalKinds.LooseCartridge].Top1Pct.ShouldBe(50);
        single.ByKind[EvalKinds.VhsSleeve].Top1Pct.ShouldBe(0);
        single.ByKind[EvalKinds.VhsSleeve].Top3Pct.ShouldBe(100);
        single.ResolvedBy["model"].ShouldBe(1);
        single.InventedTitles.ShouldBe(1); // Wave Race 64, matched, is in no truth
    }

    [Fact]
    public void A_release_whose_format_the_catalog_does_not_know_is_not_held_against_a_single_picture_match_but_a_different_format_is()
    {
        _golden.AddSingle("b1", new SyntheticGolden.SingleItem("The Quiet Orchard", "paperback", "complete", EvalKinds.BookSpine));
        _golden.AddSingle("b2", new SyntheticGolden.SingleItem("The Lantern Cartographer", "paperback", "complete", EvalKinds.BookSpine));

        var photos = new[]
        {
            Photo("b1", Apple, [new GroundedItem("the quiet orchard", null, "model", "matched", [Release("The Quiet Orchard", null, "book", "unknown")])]),
            Photo("b2", Apple, [new GroundedItem("the lantern cartographer", null, "model", "matched", [Release("The Lantern Cartographer", null, "book", "hardcover")])]),
        };
        var single = EvalScorer.Score(photos, TruthsOf(_golden)).SoloOf();

        single.TruthItems.ShouldBe(2);
        single.Top1Pct.ShouldBe(50); // the unknown-format release is the paperback; the hardcover is not
        single.Top3Pct.ShouldBe(50);
    }

    [Fact]
    public void A_single_picture_match_must_be_the_kind_of_item_the_truth_format_says_so_a_same_titled_book_is_not_the_vhs_but_the_film_is()
    {
        _golden.AddSingle("v1", new SyntheticGolden.SingleItem("The Marsh Lantern", "vhs", "complete", EvalKinds.VhsSleeve));
        _golden.AddSingle("v2", new SyntheticGolden.SingleItem("The Marsh Lantern", "vhs", "complete", EvalKinds.VhsSleeve));

        var photos = new[]
        {
            Photo("v1", Apple, [new GroundedItem("the marsh lantern", null, "model", "matched", [new GroundedCandidate("The Marsh Lantern", "book", null, null, 1997, 0.95)])]),
            Photo("v2", Apple, [new GroundedItem("the marsh lantern", null, "model", "matched", [Release("The Marsh Lantern", null, "film", "vhs")])]),
        };
        var single = EvalScorer.Score(photos, TruthsOf(_golden)).SoloOf();

        single.TruthItems.ShouldBe(2);
        single.Top1Pct.ShouldBe(50); // only the film
        single.Top3Pct.ShouldBe(50);
    }

    [Fact]
    public void A_single_picture_game_truth_is_not_matched_by_a_same_titled_film()
    {
        _golden.AddSingle("g1", new SyntheticGolden.SingleItem("Tide Runner", "game_cartridge", "cart_only", EvalKinds.LooseCartridge, "Nintendo 64"));

        var single = EvalScorer.Score(
            [Photo("g1", Apple, [new GroundedItem("tide runner", "cart_only", "model", "matched", [new GroundedCandidate("Tide Runner", "film", null, "Nintendo 64", 1997, 0.95)])])],
            TruthsOf(_golden)).SoloOf();

        single.Top1Pct.ShouldBe(0);
        single.Top3Pct.ShouldBe(0);
    }

    [Fact]
    public void A_tv_release_fits_a_film_format_and_a_release_with_no_kind_is_not_held_against_a_single_picture_match()
    {
        _golden.AddSingle("t1", new SyntheticGolden.SingleItem("Harbour Lights Season One", "dvd", "complete", EvalKinds.DiscCover));
        _golden.AddSingle("t2", new SyntheticGolden.SingleItem("Paper Moon Orchard", "dvd", "complete", EvalKinds.DiscCover));

        var photos = new[]
        {
            Photo("t1", Apple, [new GroundedItem("harbour lights season one", null, "model", "matched", [Release("Harbour Lights Season One", null, "tv", "dvd")])]),
            Photo("t2", Apple, [new GroundedItem("paper moon orchard", null, "model", "matched", [new GroundedCandidate("Paper Moon Orchard", null, "dvd", null, 1997, 0.95)])]),
        };
        var single = EvalScorer.Score(photos, TruthsOf(_golden)).SoloOf();

        single.Top1Pct.ShouldBe(100);
    }

    [Fact]
    public void In_a_single_picture_only_the_first_item_is_the_answer_and_the_others_only_cost_precision()
    {
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", EvalKinds.LooseCartridge, "Nintendo 64"));

        var single = EvalScorer.Score([Photo("c1", Apple, [NotFound(), Matched("Star Fox 64")])], TruthsOf(_golden)).SoloOf();

        single.Top1Pct.ShouldBe(0);
        single.PredictedItems.ShouldBe(2);
    }

    [Fact]
    public void Photos_without_truth_are_not_scored()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        _golden.AddShelf("untruthed", null);

        var scored = EvalScorer.Score([Photo("s1", Apple, [Matched("Star Fox 64")]), Photo("untruthed", Apple, [Matched("Anything")])], TruthsOf(_golden));

        scored.ShelfOf().Photos.ShouldBe(1);
        scored.ShelfOf().PredictedItems.ShouldBe(1);
    }

    [Fact]
    public void Kind_keys_that_are_not_plain_snake_case_are_filed_under_other_so_no_title_can_become_a_group_name()
    {
        EvalKinds.Key("loose_cartridge").ShouldBe("loose_cartridge");
        EvalKinds.Key("Star Fox 64 (the real title)").ShouldBe("other");
        EvalKinds.Key(null).ShouldBe("other");
        EvalKinds.Key(new string('a', 33)).ShouldBe("other");
    }

    [Fact]
    public void Every_book_format_guesses_a_book_spine()
    {
        EvalKinds.Guess(ReleaseFormat.Hardcover, null).ShouldBe(EvalKinds.BookSpine);
        EvalKinds.Guess(ReleaseFormat.Paperback, null).ShouldBe(EvalKinds.BookSpine);
        EvalKinds.Guess(ReleaseFormat.MassMarket, null).ShouldBe(EvalKinds.BookSpine);
        EvalKinds.Guess(ReleaseFormat.BoardBook, null).ShouldBe(EvalKinds.BookSpine);
        EvalKinds.Guess(ReleaseFormat.SpiralBound, null).ShouldBe(EvalKinds.BookSpine);
    }
}
