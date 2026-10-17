using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>The M3 gate rules (docs/02) and the 2-point regression rule (docs/09), on a golden set generated in the test.</summary>
public sealed class GateAndRegressionTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    /// <summary>One single-item picture per kind with a title, the format and the kind the gate's targets are set for.</summary>
    private static readonly (string Id, string Kind, string Title, string Format, string Media, string? Platform)[] Singles =
    [
        ("b1", EvalKinds.BookSpine, "Dune", "paperback", "book", null),
        ("g1", EvalKinds.GameBox, "Chrono Trigger", "game_disc", "game", "SNES"),
        ("d1", EvalKinds.DiscCover, "Alien", "dvd", "film", null),
        ("v1", EvalKinds.VhsSleeve, "The Matrix", "vhs", "film", null),
        ("c1", EvalKinds.LooseCartridge, "Star Fox 64", "game_cartridge", "game", "Nintendo 64"),
        ("l1", EvalKinds.BareLabel, "Jaws", "vhs", "film", null),
    ];

    private IReadOnlyDictionary<string, TruthEntry> ArrangeTruth()
    {
        foreach (var (id, kind, title, format, _, platform) in Singles)
        {
            _golden.AddSingle(id, new SyntheticGolden.SingleItem(title, format, "complete", kind, platform));
        }

        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64")]);
        return TruthsOf(_golden);
    }

    private static GroundedPhoto OnePhoto((string Id, string Kind, string Title, string Format, string Media, string? Platform) s, string engine, bool right = true, long latencyMs = 2000, string thermal = "nominal", string outcome = "ok")
    {
        var title = right ? s.Title : "Something Else Entirely";
        GroundedItem[] items = outcome == "ok"
            ? [new GroundedItem(title, "complete", engine == Apple ? "model" : "vision_text", "matched", [Release(title, s.Platform, s.Media, s.Format)])]
            : [];
        return Photo(s.Id, engine, items, latencyMs, firstItemMs: null, outcome: outcome, thermal: thermal);
    }

    private static GroundedPhoto ShelfPhoto(string engine, bool right = true, long latencyMs = 6000, long firstItemMs = 1500, string thermal = "nominal") =>
        Photo("s1", engine, right ? [Matched("Star Fox 64"), Matched("Mario Kart 64")] : [Matched("Star Fox 64")], latencyMs, firstItemMs, thermal: thermal);

    /// <summary>Apple meets every bar; Vision text gets half of the singles right.</summary>
    private static List<GroundedPhoto> MeetingEveryBar(Func<GroundedPhoto, GroundedPhoto>? changeApple = null)
    {
        var photos = new List<GroundedPhoto>();
        foreach (var s in Singles)
        {
            photos.Add(OnePhoto(s, Apple));
            photos.Add(OnePhoto(s, Vision, right: s.Id[0] is 'b' or 'g' or 'd'));
        }

        photos.Add(ShelfPhoto(Apple));
        photos.Add(ShelfPhoto(Vision, right: false));
        return changeApple is null ? photos : [.. photos.Select(p => p.Engine == Apple ? changeApple(p) : p)];
    }

    private EvaluatedRun Evaluate(IEnumerable<GroundedPhoto> photos, IReadOnlyDictionary<string, double>? baseline = null) =>
        EvalReport.Evaluate(RunOf([.. photos]), ArrangeTruth(), baseline, "abc1234", []);

    private static string StatusOf(EvaluatedRun run, string measure) => run.Summary.Gate.Measures.Single(m => m.Name == measure).Status;

    // ---- the M3 gate -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_that_meets_every_gate_measure_passes()
    {
        var run = Evaluate(MeetingEveryBar());

        run.Summary.Gate.Measures.Where(m => m.Status != M3Gate.Pass).Select(m => $"{m.Name}={m.Status} ({m.Actual})").ShouldBeEmpty();
        run.Summary.Gate.Verdict.ShouldBe("pass");
        run.Summary.Gate.Comparison.AppleBeatsVision.ShouldBe(true);
        run.Summary.Gate.Comparison.AppleTop1Pct.ShouldBe(100);
    }

    [Fact]
    public void The_gate_table_lists_every_measure_of_the_doc_with_its_bar()
    {
        var names = Evaluate(MeetingEveryBar()).Summary.Gate.Measures.Select(m => m.Name).ToList();

        names.ShouldBe(
        [
            "top1.book_spine", "top1.game_box", "top1.disc_cover", "top1.vhs_sleeve", "top1.loose_cartridge", "top1.bare_label",
            "top1.beats_vision_text", "speed.single_p50", "speed.shelf_first_item_p50", "speed.shelf_total_p50",
            "invented_titles", "guardrail_refusals", "thermals",
        ]);
    }

    [Fact]
    public void Missing_a_top_1_target_fails_that_measure_and_the_gate()
    {
        var run = Evaluate(MeetingEveryBar(p => p.PhotoId == "c1" ? OnePhoto(Singles[4], Apple, right: false) : p));

        StatusOf(run, "top1.loose_cartridge").ShouldBe("fail");
        run.Summary.Gate.Verdict.ShouldBe("fail");
    }

    [Fact]
    public void Being_slower_than_2_5_seconds_per_single_item_fails_the_speed_bar()
    {
        var run = Evaluate(MeetingEveryBar(p => p.PhotoId != "s1" ? p with { LatencyMs = 2600 } : p));

        StatusOf(run, "speed.single_p50").ShouldBe("fail");
        run.Summary.Gate.Verdict.ShouldBe("fail");
    }

    [Fact]
    public void A_first_shelf_card_after_2_seconds_or_a_shelf_after_8_seconds_fails()
    {
        var slowFirst = Evaluate(MeetingEveryBar(p => p.PhotoId == "s1" ? p with { FirstItemMs = 2100 } : p));
        var slowWhole = Evaluate(MeetingEveryBar(p => p.PhotoId == "s1" ? p with { LatencyMs = 8100 } : p));

        StatusOf(slowFirst, "speed.shelf_first_item_p50").ShouldBe("fail");
        StatusOf(slowWhole, "speed.shelf_total_p50").ShouldBe("fail");
    }

    [Fact]
    public void An_invented_title_at_or_above_1_percent_of_items_fails()
    {
        var run = Evaluate(MeetingEveryBar(p => p.PhotoId == "s1" ? p with { Items = [.. p.Items, Matched("Zorblax Quest")] } : p));

        StatusOf(run, "invented_titles").ShouldBe("fail");
        run.Summary.Gate.Verdict.ShouldBe("fail");
    }

    [Fact]
    public void A_refusal_rate_at_or_above_2_percent_of_photos_fails()
    {
        var run = Evaluate(MeetingEveryBar(p => p.PhotoId == "b1" ? OnePhoto(Singles[0], Apple, outcome: "refused") : p));

        StatusOf(run, "guardrail_refusals").ShouldBe("fail");
    }

    [Fact]
    public void A_phone_that_ends_serious_or_slows_down_late_in_the_run_fails_the_thermal_measure()
    {
        var hot = Evaluate(MeetingEveryBar(p => p.PhotoId == "s1" ? p with { ThermalState = "serious" } : p));
        var slowing = Evaluate(MeetingEveryBar(p => p.PhotoId is "l1" or "c1" ? p with { LatencyMs = 4000 } : p));

        StatusOf(hot, "thermals").ShouldBe("fail");
        StatusOf(slowing, "thermals").ShouldBe("fail");
    }

    [Fact]
    public void The_apple_model_must_beat_the_vision_text_engine_not_just_tie_it()
    {
        var tie = Evaluate(MeetingEveryBar().Where(p => p.Engine == Apple).Concat(MeetingEveryBar().Where(p => p.Engine == Apple).Select(p => p with { Engine = Vision })));

        StatusOf(tie, "top1.beats_vision_text").ShouldBe("fail");
    }

    [Fact]
    public void A_gate_with_a_measure_that_has_no_photos_yet_is_incomplete_not_a_pass()
    {
        var run = Evaluate(MeetingEveryBar().Where(p => p.Engine == Apple));

        StatusOf(run, "top1.beats_vision_text").ShouldBe("no_data");
        run.Summary.Gate.Verdict.ShouldBe("incomplete");
    }

    // ---- the regression rule ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_3_point_drop_against_the_baseline_is_flagged_and_a_1_point_drop_is_not()
    {
        var baseline = new Dictionary<string, double> { ["apple_model.single.top1"] = 90 };

        var threePoints = EvalRegression.Check(baseline, new Dictionary<string, double> { ["apple_model.single.top1"] = 87 });
        var onePoint = EvalRegression.Check(baseline, new Dictionary<string, double> { ["apple_model.single.top1"] = 89 });

        threePoints.Status.ShouldBe("regressed");
        threePoints.Regressions.Single().Metric.ShouldBe("apple_model.single.top1");
        threePoints.Regressions.Single().Points.ShouldBe(3);
        onePoint.Status.ShouldBe("ok");
        onePoint.Regressions.ShouldBeEmpty();
    }

    [Fact]
    public void Exactly_2_points_is_allowed_and_a_better_number_never_flags()
    {
        var baseline = new Dictionary<string, double> { ["a.top1"] = 90, ["a.recall"] = 80 };

        EvalRegression.Check(baseline, new Dictionary<string, double> { ["a.top1"] = 88, ["a.recall"] = 95 }).Status.ShouldBe("ok");
    }

    [Fact]
    public void For_invented_titles_and_refusals_a_rise_of_more_than_2_points_is_the_regression()
    {
        var baseline = new Dictionary<string, double> { ["apple_model.shelf.invented_pct"] = 0.5, ["apple_model.shelf.refusal_pct"] = 1 };

        EvalRegression.Check(baseline, new Dictionary<string, double> { ["apple_model.shelf.invented_pct"] = 3, ["apple_model.shelf.refusal_pct"] = 0 })
            .Regressions.Select(r => r.Metric).ShouldBe(["apple_model.shelf.invented_pct"]);
        EvalRegression.Check(baseline, new Dictionary<string, double> { ["apple_model.shelf.invented_pct"] = 0, ["apple_model.shelf.refusal_pct"] = 3.5 })
            .Regressions.Select(r => r.Metric).ShouldBe(["apple_model.shelf.refusal_pct"]);
    }

    [Fact]
    public void A_metric_the_baseline_does_not_know_is_new_not_a_regression_and_no_baseline_says_so()
    {
        EvalRegression.Check(new Dictionary<string, double> { ["old.top1"] = 90 }, new Dictionary<string, double> { ["new.top1"] = 10 }).Status.ShouldBe("ok");
        EvalRegression.Check(null, new Dictionary<string, double> { ["x"] = 1 }).Status.ShouldBe("no_baseline");
        EvalRegression.Check(new Dictionary<string, double>(), new Dictionary<string, double> { ["x"] = 1 }).Status.ShouldBe("no_baseline");
    }

    [Fact]
    public void A_run_is_checked_against_the_baseline_by_the_metric_names_the_summary_publishes()
    {
        var first = Evaluate(MeetingEveryBar());
        var baseline = first.Summary.Metrics.ToDictionary(m => m.Key, m => m.Value);
        baseline["apple_model.single.top1"] = 105; // as if the last run had been 5 points better than this one

        var run = Evaluate(MeetingEveryBar(), baseline);

        run.Summary.Regression.Status.ShouldBe("regressed");
        run.Summary.Regression.Regressions.Select(r => r.Metric).ShouldContain("apple_model.single.top1");
        Evaluate(MeetingEveryBar(), first.Summary.Metrics).Summary.Regression.Status.ShouldBe("ok");
    }
}
