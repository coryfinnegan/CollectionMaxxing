using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The review-burden maths (docs/16 §17) on a tiny synthetic golden set: per-tier counts, <c>review_rate</c> (the share of
/// truth items needing any human touch) and <c>auto_add_precision</c> (right auto-commits over T0+T1 commits, gated at 99%).
/// </summary>
public sealed class ReviewBurdenMathsTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private ScoredEngines ScoreShelf(params GroundedItem[] read)
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64"), ShelfItem("Banjo-Kazooie"), ShelfItem("Goldeneye 007")]);
        return EvalScorer.Score([Photo("s1", Apple, read)], TruthsOf(_golden));
    }

    [Fact]
    public void Tiers_and_provisionals_are_counted_per_slice()
    {
        var shelf = ScoreShelf(
            T0(Matched("Star Fox 64")),
            T1(Matched("Mario Kart 64")),
            T2(Matched("Banjo-Kazooie")),
            Kept(Matched("Goldeneye 007"))).ShelfOf();

        shelf.T0Auto.ShouldBe(1);
        shelf.T1Auto.ShouldBe(1);
        shelf.T2Review.ShouldBe(1);
        shelf.ProvisionalItems.ShouldBe(1);
    }

    [Fact]
    public void A_provisional_is_never_an_auto_add_even_when_it_also_carries_a_tier()
    {
        var shelf = ScoreShelf(T1(Kept(Matched("Star Fox 64")))).ShelfOf();

        shelf.T1Auto.ShouldBe(0);
        shelf.ProvisionalItems.ShouldBe(1);
        shelf.AutoAdds.ShouldBe(0);
        shelf.AutoAddPrecisionPct.ShouldBeNull();
    }

    [Fact]
    public void Review_rate_is_the_share_of_truth_items_no_correct_auto_add_claimed()
    {
        // Star Fox: correct T1 auto-add, no touch. Mario: T2 review. Banjo: provisional. Goldeneye: missed entirely.
        var shelf = ScoreShelf(
            T1(Matched("Star Fox 64")),
            T2(Matched("Mario Kart 64")),
            Kept(Matched("Banjo-Kazooie"))).ShelfOf();

        shelf.ReviewRatePct.ShouldBe(75); // 3 of 4 truth items need a touch
    }

    [Fact]
    public void A_wrong_auto_add_needs_a_touch_so_it_counts_in_review_rate_and_fails_precision()
    {
        // The T1 auto-add put the right title on the wrong console: a correction, not a clean commit.
        var shelf = ScoreShelf(
            T1(Matched("Star Fox 64")),
            T1(Matched("Mario Kart 64", platform: "Wii")),
            T1(Matched("Banjo-Kazooie")),
            T1(Matched("Goldeneye 007"))).ShelfOf();

        shelf.ReviewRatePct.ShouldBe(25);
        shelf.AutoAdds.ShouldBe(4);
        shelf.CorrectAutoAdds.ShouldBe(3);
        shelf.AutoAddPrecisionPct.ShouldBe(75);
    }

    [Fact]
    public void Auto_add_precision_is_null_without_auto_adds_and_100_when_every_auto_add_is_right()
    {
        ScoreShelf(T2(Matched("Star Fox 64"))).ShelfOf().AutoAddPrecisionPct.ShouldBeNull();

        var shelf = ScoreShelf(T0(Matched("Star Fox 64")), T1(Matched("Mario Kart 64"))).ShelfOf();
        shelf.AutoAdds.ShouldBe(2);
        shelf.CorrectAutoAdds.ShouldBe(2);
        shelf.AutoAddPrecisionPct.ShouldBe(100);
    }

    [Fact]
    public void A_second_copy_of_a_real_title_is_a_wrong_auto_add_not_an_invented_one()
    {
        var shelf = ScoreShelf(T1(Matched("Star Fox 64")), T1(Matched("Star Fox 64"))).ShelfOf();

        shelf.InventedTitles.ShouldBe(0);
        shelf.AutoAdds.ShouldBe(2);
        shelf.CorrectAutoAdds.ShouldBe(1);
        shelf.AutoAddPrecisionPct.ShouldBe(50);
    }

    [Fact]
    public void The_flattening_names_review_rate_auto_add_precision_and_per_tier_counts()
    {
        var metrics = EvalRegression.Flatten(ScoreShelf(T1(Matched("Star Fox 64")), T2(Matched("Mario Kart 64"))).Engines);

        metrics["apple_model.shelf.review_rate"].ShouldBe(75);
        metrics["apple_model.shelf.auto_add_precision"].ShouldBe(100);
        metrics["apple_model.shelf.t0_auto"].ShouldBe(0);
        metrics["apple_model.shelf.t1_auto"].ShouldBe(1);
        metrics["apple_model.shelf.t2_review"].ShouldBe(1);
        metrics["apple_model.shelf.provisional"].ShouldBe(0);
    }

    [Fact]
    public void For_review_rate_a_rise_is_the_regression_for_auto_adds_a_drop_is_and_the_per_tier_counts_are_not_judged()
    {
        var baseline = new Dictionary<string, double>
        {
            ["apple_model.shelf.review_rate"] = 20,
            ["apple_model.shelf.t2_review"] = 5,
            ["apple_model.shelf.auto_add_precision"] = 100,
            ["apple_model.shelf.t1_auto"] = 10,
        };

        var worse = EvalRegression.Check(baseline, new Dictionary<string, double>
        {
            ["apple_model.shelf.review_rate"] = 23,
            ["apple_model.shelf.t2_review"] = 8,
            ["apple_model.shelf.auto_add_precision"] = 97,
            ["apple_model.shelf.t1_auto"] = 7,
        });

        worse.Status.ShouldBe(EvalRegression.Regressed);
        worse.Regressions.Select(r => r.Metric).ShouldBe(
        [
            "apple_model.shelf.auto_add_precision",
            "apple_model.shelf.review_rate",
        ]);
        worse.Compared.ShouldBe(2); // the two counts were not judged
    }

    [Fact]
    public void The_gate_fails_below_99_percent_auto_add_precision_and_passes_at_99()
    {
        var below = M3Gate.Evaluate(new Dictionary<string, EngineSummary>
        {
            [EvalCombination.Engine] = new EngineSummary(
                new SliceMetrics(0, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, null, 0, null, 0, null, 0, 0, null, 0, null, null, null, null,
                    new Dictionary<string, KindMetrics>(), new Dictionary<string, int>(), new Dictionary<string, KindMetrics>(),
                    T1Auto: 200, AutoAdds: 200, CorrectAutoAdds: 197, AutoAddPrecisionPct: 98.5),
                null, null, null),
        });

        below.Measures.Single(m => m.Name == "auto_add_precision").Status.ShouldBe(M3Gate.Fail);
        below.Verdict.ShouldBe(M3Gate.Fail);

        var atBar = M3Gate.Evaluate(new Dictionary<string, EngineSummary>
        {
            [EvalCombination.Engine] = new EngineSummary(
                new SliceMetrics(0, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, null, 0, null, 0, null, 0, 0, null, 0, null, null, null, null,
                    new Dictionary<string, KindMetrics>(), new Dictionary<string, int>(), new Dictionary<string, KindMetrics>(),
                    T1Auto: 100, AutoAdds: 100, CorrectAutoAdds: 99, AutoAddPrecisionPct: 99),
                null, null, null),
        });

        atBar.Measures.Single(m => m.Name == "auto_add_precision").Status.ShouldBe(M3Gate.Pass);
    }
}
