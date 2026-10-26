using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>Truth files may label each picture's shot kind; scoring then reports top-1 and top-3 per kind.</summary>
public sealed class ShotKindSlicingTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    [Fact]
    public void A_truth_file_with_a_shot_kind_parses_and_unknown_keys_are_ignored()
    {
        var shelf = TruthFiles.Parse(
            "s1",
            GoldenSets.Shelf,
            """{ "items": [{ "title": "Star Fox 64", "mediaType": "game", "platform": "Nintendo 64" }], "unreadable": 0, "shotKind": "shelf_segment", "somethingNew": { "x": 1 } }""");
        var single = TruthFiles.Parse(
            "c1",
            GoldenSets.Single,
            """{ "title": "Star Fox 64", "format": "game_cartridge", "completeness": "cart_only", "kind": "loose_cartridge", "shotKind": "loose_item", "future": [1, 2] }""");

        shelf.ShouldNotBeNull();
        shelf.ShotKind.ShouldBe("shelf_segment");
        shelf.Shelf!.Items.Single().Title.ShouldBe("Star Fox 64");
        single.ShouldNotBeNull();
        single.ShotKind.ShouldBe("loose_item");
    }

    [Fact]
    public void A_truth_file_without_a_shot_kind_still_parses()
    {
        var shelf = TruthFiles.Parse(
            "s1", GoldenSets.Shelf, """{ "items": [{ "title": "Star Fox 64", "mediaType": "game" }] }""");

        shelf.ShouldNotBeNull();
        shelf.ShotKind.ShouldBeNull();
        shelf.Shelf!.Items.Single().Title.ShouldBe("Star Fox 64");
    }

    [Fact]
    public void Scoring_reports_top_1_and_top_3_per_truth_shot_kind()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64")], shotKind: "shelf_segment");
        _golden.AddShelf("s2", [ShelfItem("Banjo-Kazooie"), ShelfItem("Goldeneye 007")], shotKind: "single_cover");

        var shelf = EvalScorer.Score(
            [
                Photo("s1", Apple, [Matched("Star Fox 64"), Matched("Mario Kart 64")]),
                Photo("s2", Apple, [Matched("Banjo-Kazooie"), NotFound("smudged spine")]),
            ],
            TruthsOf(_golden)).ShelfOf();

        shelf.ByShotKind["shelf_segment"].ShouldBe(new KindMetrics(2, 2, 2, 100, 100));
        shelf.ByShotKind["single_cover"].ShouldBe(new KindMetrics(2, 1, 1, 50, 50));
    }

    [Fact]
    public void Photos_without_a_labelled_shot_kind_are_sliced_as_unknown()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);

        var shelf = EvalScorer.Score([Photo("s1", Apple, [Matched("Star Fox 64")])], TruthsOf(_golden)).ShelfOf();

        shelf.ByShotKind.Keys.ShouldBe(["unknown"]);
        shelf.ByShotKind["unknown"].ShouldBe(new KindMetrics(1, 1, 1, 100, 100));
    }

    [Fact]
    public void The_regression_flattening_names_shot_kind_slices()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")], shotKind: "shelf_segment");

        var metrics = EvalRegression.Flatten(EvalScorer.Score([Photo("s1", Apple, [Matched("Star Fox 64")])], TruthsOf(_golden)).Engines);

        metrics["apple_model.shelf.shotkind.shelf_segment.top1"].ShouldBe(100);
        metrics["apple_model.shelf.shotkind.shelf_segment.top3"].ShouldBe(100);
    }
}
