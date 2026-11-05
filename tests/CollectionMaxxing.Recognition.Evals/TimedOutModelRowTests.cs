using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>A model read that ran out of time but kept its finished items goes out ok and says so: it is a timeout in the rates and still scores its items.</summary>
public sealed class TimedOutModelRowTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private static GroundedPhoto TimedOutRead(string id, long latencyMs = 31_600, string engine = Apple) =>
        Photo(id, engine, [Matched("Star Fox 64", completeness: "cart_only")], latencyMs) with { TimedOut = true };

    private IReadOnlyDictionary<string, TruthEntry> ArrangeSingle()
    {
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", EvalKinds.LooseCartridge, "Nintendo 64"));
        return TruthsOf(_golden);
    }

    [Fact]
    public void An_ok_model_row_that_timed_out_with_items_counts_as_a_timeout_and_still_scores_its_items()
    {
        var single = EvalScorer.Score([TimedOutRead("c1")], ArrangeSingle()).SoloOf();

        single.Timeouts.ShouldBe(1);
        single.TimeoutPct.ShouldBe(100);
        single.Top1Hits.ShouldBe(1);
        single.PredictedItems.ShouldBe(1);
        single.Failures.ShouldBe(0);
    }

    [Fact]
    public void A_timed_out_row_counts_once_in_the_latency_at_its_measured_time()
    {
        var single = EvalScorer.Score([TimedOutRead("c1", latencyMs: 31_600)], ArrangeSingle()).SoloOf();

        single.LatencyP95Ms.ShouldBe(31_600);
    }

    [Fact]
    public void A_timed_out_row_measured_under_the_budget_counts_at_the_budget()
    {
        var single = EvalScorer.Score([TimedOutRead("c1", latencyMs: 3000)], ArrangeSingle()).SoloOf();

        single.LatencyP95Ms.ShouldBe(EvalScorer.ModelTimeoutBudgetMs);
    }

    [Fact]
    public void An_ok_row_from_an_older_build_with_no_flag_scores_as_it_did()
    {
        var single = EvalScorer.Score([Photo("c1", Apple, [Matched("Star Fox 64", completeness: "cart_only")], 31_600)], ArrangeSingle()).SoloOf();

        single.Timeouts.ShouldBe(0);
        single.LatencyP95Ms.ShouldBe(31_600);
    }

    [Fact]
    public void Model_timeouts_counts_it()
    {
        var gate = M3Gate.Evaluate(EvalScorer.Score([TimedOutRead("c1")], ArrangeSingle()).Engines);

        gate.Measures.Single(m => m.Name == "model_timeouts").Actual.ShouldBe("100%");
    }

    [Fact]
    public void The_fm_text_row_that_timed_out_counts_as_a_timeout_too()
    {
        var single = EvalScorer.Score([TimedOutRead("c1", engine: "fm_text")], ArrangeSingle()).SoloOf("fm_text");

        single.Timeouts.ShouldBe(1);
        single.Top1Hits.ShouldBe(1);
    }

    [Fact]
    public void The_combined_shelf_row_keeps_the_models_items_and_is_not_itself_a_timeout()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64")]);
        var model = Photo("s1", Apple, [T1(Matched("Star Fox 64")), T1(Matched("Mario Kart 64"))], 31_600, firstItemMs: 900) with { TimedOut = true };
        var vision = Photo("s1", Vision, [], 700, firstItemMs: null);

        var scored = EvalScorer.Score([model, vision], TruthsOf(_golden));

        scored.ShelfOf(Apple).Timeouts.ShouldBe(1);
        var combined = scored.ShelfOf(EvalCombination.Engine);
        combined.Timeouts.ShouldBe(0);
        combined.Top1Hits.ShouldBe(2);
    }
}
