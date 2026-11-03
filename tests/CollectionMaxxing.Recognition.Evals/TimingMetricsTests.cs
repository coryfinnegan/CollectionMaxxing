using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The wall-clock maths (docs/16 §17), measured from the harness timestamps, not self-reported. A trustworthy record is a correctly resolved item;
/// a run that resolves nothing is never trustworthy. Two different questions, two different measures (issue #629):
/// <list type="bullet">
/// <item><c>time_to_first_results</c> is the scanner's: each photo's own capture to its first correct item, reported as the slice's median (p50) and
/// p95. One cold first read is one photo, so it moves the p95 and never the median the regression check judges.</item>
/// <item><c>total_time_to_trustworthy</c> is the run's: the first capture to the last correct item, owner pace included. It is what the home
/// acceptance scenario sets against a barcode pass of the same shelf (docs/16 §17), so it stays whole, and the regression check does not judge it.</item>
/// </list>
/// </summary>
public sealed class TimingMetricsTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    private static readonly DateTimeOffset Start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private const string FirstP50 = "apple_model.shelf.time_to_first_results_p50";
    private const string FirstP95 = "apple_model.shelf.time_to_first_results_p95";
    private const string Total = "apple_model.shelf.total_time_to_trustworthy";

    // What the old rule called the slice's first results: the whole slice's first capture to its first correct photo.
    private const string OldFirstResults = "apple_model.shelf.time_to_first_results";

    private static readonly string[] Titles = ["Star Fox 64", "Mario Kart 64", "Banjo-Kazooie", "GoldenEye 007", "Paper Mario"];

    public void Dispose() => _golden.Dispose();

    private ScoredEngines Score(params GroundedPhoto[] photos)
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        _golden.AddShelf("s2", [ShelfItem("Mario Kart 64")]);
        return EvalScorer.Score(photos, TruthsOf(_golden));
    }

    /// <summary>A shelf photo per title, each read correctly: the first photo's latency, then <paramref name="restLatencyMs"/> for the others, captured <paramref name="gapSeconds"/> apart.</summary>
    private ScoredEngines ScoreRun(long firstLatencyMs, long restLatencyMs, int gapSeconds)
    {
        var photos = new List<GroundedPhoto>();
        for (var i = 0; i < Titles.Length; i++)
        {
            _golden.AddShelf($"s{i + 1}", [ShelfItem(Titles[i])]);
            photos.Add(Photo($"s{i + 1}", Apple, [Matched(Titles[i])], latencyMs: i == 0 ? firstLatencyMs : restLatencyMs, capturedAt: Start.AddSeconds(i * gapSeconds)));
        }

        return EvalScorer.Score(photos, TruthsOf(_golden));
    }

    [Fact]
    public void First_results_are_each_photos_own_span_and_trustworthy_runs_from_the_first_capture_to_the_last_result()
    {
        var scored = Score(
            Photo("s1", Apple, [Matched("Star Fox 64")], latencyMs: 1000, capturedAt: Start),
            Photo("s2", Apple, [Matched("Mario Kart 64")], latencyMs: 3000, capturedAt: Start.AddSeconds(10))).ShelfOf();

        scored.TimeToFirstResultsP50Ms.ShouldBe(1000); // nearest-rank median of the two photos' own spans, 1 s and 3 s
        scored.TimeToFirstResultsP95Ms.ShouldBe(3000);
        scored.TotalTimeToTrustworthyMs.ShouldBe(13000); // the last photo's results, thirteen seconds after the first capture
    }

    [Fact]
    public void A_photo_without_a_correct_item_is_not_a_trustworthy_record()
    {
        var scored = Score(
            Photo("s1", Apple, [Matched("Star Fox 64")], latencyMs: 1000, capturedAt: Start),
            Photo("s2", Apple, [NotFound("smudged spine")], latencyMs: 5000, capturedAt: Start.AddSeconds(2))).ShelfOf();

        scored.TimeToFirstResultsP50Ms.ShouldBe(1000);
        scored.TimeToFirstResultsP95Ms.ShouldBe(1000); // the second photo gave no result to wait for
        scored.TotalTimeToTrustworthyMs.ShouldBe(1000); // only the first photo resolved anything
    }

    [Fact]
    public void A_run_that_resolves_nothing_or_carries_no_stamps_has_no_trustworthy_time()
    {
        var nothingRight = Score(
            Photo("s1", Apple, [NotFound()], latencyMs: 1000, capturedAt: Start),
            Photo("s2", Apple, [NotFound()], latencyMs: 1000, capturedAt: Start.AddSeconds(1))).ShelfOf();

        nothingRight.TimeToFirstResultsP50Ms.ShouldBeNull();
        nothingRight.TimeToFirstResultsP95Ms.ShouldBeNull();
        nothingRight.TotalTimeToTrustworthyMs.ShouldBeNull();

        var noStamps = Score(
            Photo("s1", Apple, [Matched("Star Fox 64")], latencyMs: 1000),
            Photo("s2", Apple, [Matched("Mario Kart 64")], latencyMs: 1000)).ShelfOf();

        noStamps.TimeToFirstResultsP50Ms.ShouldBeNull();
        noStamps.TimeToFirstResultsP95Ms.ShouldBeNull();
        noStamps.TotalTimeToTrustworthyMs.ShouldBeNull();
    }

    [Fact]
    public void A_slow_first_photo_moves_the_p95_but_not_the_median_first_results()
    {
        // The run of 2026-11-01: the model's first read took about 31 s (the engine loading), every other photo about 1 s.
        var shelf = ScoreRun(firstLatencyMs: 31_000, restLatencyMs: 1_000, gapSeconds: 40).ShelfOf();

        shelf.TimeToFirstResultsP50Ms.ShouldBe(1000);
        shelf.TimeToFirstResultsP95Ms.ShouldBe(31_000);
        shelf.TotalTimeToTrustworthyMs.ShouldBe(161_000); // the run: the last photo, captured 160 s in, was read 1 s later
    }

    [Fact]
    public void The_time_the_owner_takes_between_photos_does_not_move_first_results()
    {
        var quick = ScoreRun(firstLatencyMs: 1_000, restLatencyMs: 1_000, gapSeconds: 2).ShelfOf();
        var slow = ScoreRun(firstLatencyMs: 1_000, restLatencyMs: 1_000, gapSeconds: 300).ShelfOf();

        slow.TimeToFirstResultsP50Ms.ShouldBe(quick.TimeToFirstResultsP50Ms);
        slow.TimeToFirstResultsP95Ms.ShouldBe(quick.TimeToFirstResultsP95Ms);
        slow.TotalTimeToTrustworthyMs!.Value.ShouldBeGreaterThan(quick.TotalTimeToTrustworthyMs!.Value); // it is the run that takes longer
    }

    [Fact]
    public void The_flattening_names_the_times_and_a_slower_median_is_the_regression()
    {
        var metrics = EvalRegression.Flatten(Score(
            Photo("s1", Apple, [Matched("Star Fox 64")], latencyMs: 1000, capturedAt: Start),
            Photo("s2", Apple, [Matched("Mario Kart 64")], latencyMs: 3000, capturedAt: Start.AddSeconds(10))).Engines);

        metrics[FirstP50].ShouldBe(1);
        metrics[FirstP95].ShouldBe(3);
        metrics[Total].ShouldBe(13);
        metrics.ContainsKey(OldFirstResults).ShouldBeFalse(); // the old whole-slice span is no longer published under its old name

        var baseline = new Dictionary<string, double> { [FirstP50] = 1 };
        EvalRegression.Check(baseline, metrics.ToDictionary(m => m.Key, m => m.Value)).Status.ShouldBe(EvalRegression.Ok);
        var slower = metrics.ToDictionary(m => m.Key, m => m.Value);
        slower[FirstP50] = 3; // more than double the baseline: past the allowed ratio (a time is judged by ratio, not by points)
        var check = EvalRegression.Check(baseline, slower);

        check.Status.ShouldBe(EvalRegression.Regressed);
        check.Regressions.Single().Metric.ShouldBe(FirstP50);
    }

    [Fact]
    public void A_run_with_a_slow_first_photo_is_not_flagged_against_a_baseline_of_fast_photos()
    {
        var fast = EvalRegression.Flatten(ScoreRun(firstLatencyMs: 1_000, restLatencyMs: 1_000, gapSeconds: 40).Engines);
        var withColdStart = EvalRegression.Flatten(ScoreRun(firstLatencyMs: 31_000, restLatencyMs: 1_000, gapSeconds: 40).Engines);

        var check = EvalRegression.Check(fast.ToDictionary(m => m.Key, m => m.Value), withColdStart.ToDictionary(m => m.Key, m => m.Value));

        check.Status.ShouldBe(EvalRegression.Ok);
        check.Regressions.ShouldBeEmpty();
        withColdStart[FirstP95].ShouldBe(31); // the slow read is still in the numbers, in the p95
        fast[FirstP95].ShouldBe(1);
    }

    [Fact]
    public void The_run_time_and_the_p95_are_reported_but_not_judged()
    {
        EvalRegression.IsJudged(FirstP50).ShouldBeTrue();
        EvalRegression.IsJudged(FirstP95).ShouldBeFalse();
        EvalRegression.IsJudged(Total).ShouldBeFalse();

        var baseline = new Dictionary<string, double> { [FirstP50] = 1, [FirstP95] = 1, [Total] = 100 };
        var actual = new Dictionary<string, double> { [FirstP50] = 1, [FirstP95] = 31, [Total] = 900 };

        var check = EvalRegression.Check(baseline, actual);

        check.Status.ShouldBe(EvalRegression.Ok);
        check.Compared.ShouldBe(1); // only the median was judged
        check.Regressions.ShouldBeEmpty();
    }

    // ---- a baseline from before the change ---------------------------------------------------------------------------------

    [Fact]
    public void A_baseline_time_to_first_results_from_before_the_change_is_skipped_and_named_not_compared_with_the_new_median()
    {
        var baseline = new Dictionary<string, double>
        {
            [OldFirstResults] = 11.45, // the whole slice's first capture to its first correct photo
            ["apple_model.shelf.top1"] = 90,
        };
        var actual = new Dictionary<string, double> { [FirstP50] = 31.58, ["apple_model.shelf.top1"] = 90 };

        var check = EvalRegression.Check(baseline, actual);

        check.Status.ShouldBe(EvalRegression.Ok); // 31.58 against 11.45 would have been flagged: the two numbers are not the same measure
        check.Compared.ShouldBe(1);
        var skipped = check.Skipped.ShouldNotBeNull().ShouldHaveSingleItem();
        skipped.Metric.ShouldBe(OldFirstResults);
        skipped.Reason.ShouldContain("definition");
    }

    [Fact]
    public void A_baseline_time_to_first_results_is_skipped_even_when_the_run_still_reports_the_old_name()
    {
        var baseline = new Dictionary<string, double> { [OldFirstResults] = 11.45, ["apple_model.shelf.top1"] = 90 };
        var actual = new Dictionary<string, double> { [OldFirstResults] = 80, ["apple_model.shelf.top1"] = 90 };

        var check = EvalRegression.Check(baseline, actual);

        check.Regressions.ShouldBeEmpty();
        check.Skipped.ShouldNotBeNull().Select(s => s.Metric).ShouldBe([OldFirstResults]);
        EvalRegression.IsJudged(OldFirstResults).ShouldBeFalse();
    }

    [Fact]
    public void A_baseline_with_only_old_definition_times_compared_nothing_and_says_so_without_passing()
    {
        var check = EvalRegression.Check(
            new Dictionary<string, double> { [OldFirstResults] = 11.45 },
            new Dictionary<string, double> { [FirstP50] = 1 });

        check.Status.ShouldBe(EvalRegression.NoOverlap);
        check.Skipped.ShouldNotBeNull().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_baseline_taken_with_the_new_definition_skips_nothing()
    {
        var baseline = new Dictionary<string, double> { [FirstP50] = 1, [Total] = 100 };

        var check = EvalRegression.Check(baseline, new Dictionary<string, double> { [FirstP50] = 1.2, [Total] = 100 });

        check.Status.ShouldBe(EvalRegression.Ok);
        (check.Skipped ?? []).ShouldBeEmpty();
    }

    [Fact]
    public void The_report_says_which_baseline_times_were_not_compared_and_why()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        var baseline = new Dictionary<string, double>
        {
            [OldFirstResults] = 11.45,
            ["apple_model.single.time_to_first_results"] = 8.64,
            ["apple_model.shelf.top1"] = 0,
        };

        var run = EvalReport.Evaluate(
            RunOf(Photo("s1", Apple, [Matched("Star Fox 64")], latencyMs: 31_000, capturedAt: Start)),
            TruthsOf(_golden), baseline, "abc1234", []);

        run.Summary.Regression.Regressions.ShouldBeEmpty();
        run.Summary.Regression.Skipped.ShouldNotBeNull().Select(s => s.Metric)
            .ShouldBe(["apple_model.shelf.time_to_first_results", "apple_model.single.time_to_first_results"]);
        var section = run.ReportMarkdown[run.ReportMarkdown.IndexOf("## Regression check", StringComparison.Ordinal)..];
        section.ShouldContain("Not compared");
        section.ShouldContain("apple_model.shelf.time_to_first_results");
        section.ShouldContain("apple_model.single.time_to_first_results");
        section.ShouldContain("change of its definition");
    }
}
