using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>A model timeout is the slowest read there is, so it counts in the latency percentiles and has its own rate.</summary>
public sealed class TimeoutScoringTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private List<GroundedPhoto> TenSingles(int timeouts, long timeoutMeasuredMs = 8000)
    {
        var photos = new List<GroundedPhoto>();
        for (var i = 0; i < 10; i++)
        {
            var id = $"c{i}";
            _golden.AddSingle(id, new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", EvalKinds.LooseCartridge, "Nintendo 64"));
            photos.Add(i < timeouts
                ? Photo(id, Apple, [], timeoutMeasuredMs, firstItemMs: null, outcome: "timeout", fellBackFrom: "timeout")
                : Photo(id, Apple, [Matched("Star Fox 64", completeness: "cart_only")], 1000));
        }

        return photos;
    }

    private ScoredEngines ScoreTenSingles(int timeouts, long timeoutMeasuredMs = 8000) =>
        EvalScorer.Score(TenSingles(timeouts, timeoutMeasuredMs), TruthsOf(_golden));

    [Fact]
    public void A_timeout_counts_in_the_latency_percentiles_at_its_measured_time()
    {
        var single = ScoreTenSingles(timeouts: 1, timeoutMeasuredMs: 9500).SoloOf();

        single.LatencyP95Ms.ShouldBe(9500);
        single.LatencyP50Ms.ShouldBe(1000);
    }

    [Fact]
    public void A_timeout_that_was_measured_under_the_budget_counts_at_the_budget()
    {
        var single = ScoreTenSingles(timeouts: 1, timeoutMeasuredMs: 3000).SoloOf();

        single.LatencyP95Ms.ShouldBe(EvalScorer.ModelTimeoutBudgetMs);
    }

    [Fact]
    public void A_run_where_a_tenth_of_the_photos_time_out_has_a_worse_p95_than_the_same_run_without_them()
    {
        var photos = TenSingles(timeouts: 1);
        var truths = TruthsOf(_golden);

        var withTimeout = EvalScorer.Score(photos, truths).SoloOf();
        var without = EvalScorer.Score([.. photos.Where(p => p.Outcome != "timeout")], truths).SoloOf();

        withTimeout.LatencyP95Ms!.Value.ShouldBeGreaterThan(without.LatencyP95Ms!.Value);
    }

    [Fact]
    public void Timeouts_have_their_own_rate_and_are_not_failures_or_refusals()
    {
        var single = ScoreTenSingles(timeouts: 2).SoloOf();

        single.Timeouts.ShouldBe(2);
        single.TimeoutPct.ShouldBe(20);
        single.Failures.ShouldBe(0);
        single.Refusals.ShouldBe(0);
        single.Top1Pct.ShouldBe(80);
    }

    [Fact]
    public void An_error_still_stays_out_of_the_latency_percentiles()
    {
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", EvalKinds.LooseCartridge, "Nintendo 64"));
        var single = EvalScorer.Score([Photo("c1", Apple, [], 20000, firstItemMs: null, outcome: "error")], TruthsOf(_golden)).SoloOf();

        single.LatencyP95Ms.ShouldBeNull();
        single.Failures.ShouldBe(1);
        single.Timeouts.ShouldBe(0);
    }

    [Fact]
    public void The_gate_table_shows_the_timeout_rate()
    {
        var gate = M3Gate.Evaluate(ScoreTenSingles(timeouts: 1).Engines);

        gate.Measures.Single(m => m.Name == "model_timeouts").Actual.ShouldBe("10%");
    }
}
