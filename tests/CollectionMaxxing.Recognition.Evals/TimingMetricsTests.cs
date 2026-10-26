using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The wall-clock maths (docs/16 §17): <c>time_to_first_results</c> and <c>total_time_to_trustworthy</c> run from the first
/// capture to the first/last trustworthy record, measured from the harness timestamps, not self-reported.
/// A trustworthy record is a correctly resolved item; a run that resolves nothing is never trustworthy.
/// </summary>
public sealed class TimingMetricsTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    private static readonly DateTimeOffset Start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => _golden.Dispose();

    private ScoredEngines Score(params GroundedPhoto[] photos)
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        _golden.AddShelf("s2", [ShelfItem("Mario Kart 64")]);
        return EvalScorer.Score(photos, TruthsOf(_golden));
    }

    [Fact]
    public void First_results_and_trustworthy_span_come_from_capture_stamps_plus_latency()
    {
        var scored = Score(
            Photo("s1", Apple, [Matched("Star Fox 64")], latencyMs: 1000, capturedAt: Start),
            Photo("s2", Apple, [Matched("Mario Kart 64")], latencyMs: 3000, capturedAt: Start.AddSeconds(10))).ShelfOf();

        scored.TimeToFirstResultsMs.ShouldBe(1000); // the first photo's results, a second after the first capture
        scored.TotalTimeToTrustworthyMs.ShouldBe(13000); // the last photo's results, thirteen seconds after the first capture
    }

    [Fact]
    public void A_photo_without_a_correct_item_is_not_a_trustworthy_record()
    {
        var scored = Score(
            Photo("s1", Apple, [Matched("Star Fox 64")], latencyMs: 1000, capturedAt: Start),
            Photo("s2", Apple, [NotFound("smudged spine")], latencyMs: 5000, capturedAt: Start.AddSeconds(2))).ShelfOf();

        scored.TimeToFirstResultsMs.ShouldBe(1000);
        scored.TotalTimeToTrustworthyMs.ShouldBe(1000); // only the first photo resolved anything
    }

    [Fact]
    public void A_run_that_resolves_nothing_or_carries_no_stamps_has_no_trustworthy_time()
    {
        var nothingRight = Score(
            Photo("s1", Apple, [NotFound()], latencyMs: 1000, capturedAt: Start),
            Photo("s2", Apple, [NotFound()], latencyMs: 1000, capturedAt: Start.AddSeconds(1))).ShelfOf();

        nothingRight.TimeToFirstResultsMs.ShouldBeNull();
        nothingRight.TotalTimeToTrustworthyMs.ShouldBeNull();

        var noStamps = Score(
            Photo("s1", Apple, [Matched("Star Fox 64")], latencyMs: 1000),
            Photo("s2", Apple, [Matched("Mario Kart 64")], latencyMs: 1000)).ShelfOf();

        noStamps.TimeToFirstResultsMs.ShouldBeNull();
        noStamps.TotalTimeToTrustworthyMs.ShouldBeNull();
    }

    [Fact]
    public void The_flattening_names_both_times_and_a_slower_run_is_the_regression()
    {
        var metrics = EvalRegression.Flatten(Score(
            Photo("s1", Apple, [Matched("Star Fox 64")], latencyMs: 1000, capturedAt: Start),
            Photo("s2", Apple, [Matched("Mario Kart 64")], latencyMs: 3000, capturedAt: Start.AddSeconds(10))).Engines);

        metrics["apple_model.shelf.time_to_first_results"].ShouldBe(1);
        metrics["apple_model.shelf.total_time_to_trustworthy"].ShouldBe(13);

        var baseline = new Dictionary<string, double>
        {
            ["apple_model.shelf.time_to_first_results"] = 1,
            ["apple_model.shelf.total_time_to_trustworthy"] = 13,
        };
        EvalRegression.Check(baseline, metrics.ToDictionary(m => m.Key, m => m.Value)).Status.ShouldBe(EvalRegression.Ok);
        var slower = metrics.ToDictionary(m => m.Key, m => m.Value);
        slower["apple_model.shelf.total_time_to_trustworthy"] = 15.5; // two and a half seconds slower: past the 2-point band
        var check = EvalRegression.Check(baseline, slower);

        check.Status.ShouldBe(EvalRegression.Regressed);
        check.Regressions.Single().Metric.ShouldBe("apple_model.shelf.total_time_to_trustworthy");
    }
}
