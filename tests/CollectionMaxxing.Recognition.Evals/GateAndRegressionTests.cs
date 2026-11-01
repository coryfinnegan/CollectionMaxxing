using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The M3 gate rules (docs/02) and the regression rule (docs/09, docs/16 §17), on a golden set generated in the test. Since ADR-032 the gate
/// measures the combined row (<c>apple_vision</c>, what the app shows), not the Apple model alone.
/// </summary>
public sealed class GateAndRegressionTests : IDisposable
{
    private const string Combined = EvalCombination.Engine;

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
            ? [T1(new GroundedItem(title, "complete", engine == Apple ? "model" : "vision_text", "matched", [Release(title, s.Platform, s.Media, s.Format)]))]
            : [];
        return Photo(s.Id, engine, items, latencyMs, firstItemMs: null, outcome: outcome, thermal: thermal);
    }

    private static GroundedPhoto ShelfPhoto(string engine, bool right = true, long latencyMs = 6000, long firstItemMs = 1500, string thermal = "nominal") =>
        Photo("s1", engine, right ? [T1(Matched("Star Fox 64")), T1(Matched("Mario Kart 64"))] : [T1(Matched("Star Fox 64"))], latencyMs, firstItemMs, thermal: thermal);

    /// <summary>
    /// The model meets every bar; Vision text gets half of the singles right (all of them, and the whole shelf, with
    /// <paramref name="visionReadsAllRight"/>). On the shelf Vision reads in 1.5 s and the model in 6 s, so the combined row, which waits
    /// for both, takes 7.5 s: under the 8 s bar.
    /// </summary>
    private static List<GroundedPhoto> MeetingEveryBar(
        Func<GroundedPhoto, GroundedPhoto>? changeApple = null,
        Func<GroundedPhoto, GroundedPhoto>? changeVision = null,
        bool visionReadsAllRight = false)
    {
        var photos = new List<GroundedPhoto>();
        foreach (var s in Singles)
        {
            photos.Add(OnePhoto(s, Apple));
            photos.Add(OnePhoto(s, Vision, right: visionReadsAllRight || s.Id[0] is 'b' or 'g' or 'd'));
        }

        photos.Add(ShelfPhoto(Apple));
        photos.Add(ShelfPhoto(Vision, right: visionReadsAllRight, latencyMs: 1500, firstItemMs: 1000));
        return
        [
            .. photos.Select(p =>
                p.Engine == Apple && changeApple is not null ? changeApple(p)
                : p.Engine == Vision && changeVision is not null ? changeVision(p)
                : p),
        ];
    }

    private EvaluatedRun Evaluate(IEnumerable<GroundedPhoto> photos, IReadOnlyDictionary<string, double>? baseline = null) =>
        EvalReport.Evaluate(RunOf([.. photos]), ArrangeTruth(), baseline, "abc1234", []);

    private static string StatusOf(EvaluatedRun run, string measure) => run.Summary.Gate.Measures.Single(m => m.Name == measure).Status;

    // ---- the M3 gate -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_that_meets_every_gate_measure_passes()
    {
        var run = Evaluate(MeetingEveryBar());

        run.Summary.Gate.Measures.Where(m => m.Status is not (M3Gate.Pass or M3Gate.Info)).Select(m => $"{m.Name}={m.Status} ({m.Actual})").ShouldBeEmpty();
        run.Summary.Gate.Verdict.ShouldBe("pass");
        run.Summary.Gate.Engine.ShouldBe(Combined);
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
            "top3.book_spine", "top3.game_box", "top3.disc_cover", "top3.vhs_sleeve", "top3.loose_cartridge", "top3.bare_label",
            "top1.beats_vision_text", "speed.single_p50", "speed.shelf_first_item_p50", "speed.shelf_total_p50",
            "invented_titles", "auto_add_precision", "guardrail_refusals", "model_timeouts", "thermals",
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
    public void Meeting_every_top_1_target_but_missing_a_top_3_target_fails_the_gate_and_names_the_measure()
    {
        // 8 of 10 loose cartridges are right: 80% top-1 meets the 75% bar, 80% top-3 misses the 90% bar.
        var passing = Evaluate(MeetingEveryBar()).Summary.Engines;
        var single = passing[Combined].Single!;
        var byKind = single.ByKind.ToDictionary(k => k.Key, k => k.Value);
        byKind[EvalKinds.LooseCartridge] = new KindMetrics(10, 8, 8, 80, 80);
        var engines = passing.ToDictionary(e => e.Key, e => e.Value);
        engines[Combined] = engines[Combined] with { Single = single with { ByKind = byKind } };

        var gate = M3Gate.Evaluate(engines);

        gate.Measures.Single(m => m.Name == "top1.loose_cartridge").Status.ShouldBe("pass");
        gate.Measures.Where(m => m.Status == "fail").Select(m => m.Name).ShouldBe(["top3.loose_cartridge"]);
        gate.Verdict.ShouldBe("fail");
    }

    private GateTable GateWith(Func<SliceMetrics, SliceMetrics> changeSingle)
    {
        var engines = Evaluate(MeetingEveryBar()).Summary.Engines.ToDictionary(e => e.Key, e => e.Value);
        engines[Combined] = engines[Combined] with { Shelf = null, Single = changeSingle(engines[Combined].Single!) };
        return M3Gate.Evaluate(engines);
    }

    [Theory]
    [InlineData(200, 1, "pass")] // 0.5% of items
    [InlineData(101, 1, "pass")] // 0.99%
    [InlineData(100, 1, "fail")] // exactly 1%: the bar is "under 1%"
    [InlineData(99, 1, "fail")] // one over
    public void Invented_titles_must_be_strictly_under_1_percent_of_items(int predicted, int invented, string expected)
    {
        var gate = GateWith(s => s with { PredictedItems = predicted, InventedTitles = invented });

        gate.Measures.Single(m => m.Name == "invented_titles").Status.ShouldBe(expected);
    }

    [Theory]
    [InlineData(101, 2, "pass")] // 1.98%
    [InlineData(50, 1, "fail")] // exactly 2%: the bar is "under 2%"
    [InlineData(49, 1, "fail")] // one over
    public void Guardrail_refusals_must_be_strictly_under_2_percent_of_photos(int photos, int refusals, string expected)
    {
        var gate = GateWith(s => s with { Photos = photos, Refusals = refusals });

        gate.Measures.Single(m => m.Name == "guardrail_refusals").Status.ShouldBe(expected);
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
        // The first card on a shelf is Vision's, so it is Vision's first item that is timed.
        var slowFirst = Evaluate(MeetingEveryBar(changeVision: p => p.PhotoId == "s1" ? p with { FirstItemMs = 2100 } : p));
        var slowWhole = Evaluate(MeetingEveryBar(p => p.PhotoId == "s1" ? p with { LatencyMs = 8100 } : p));

        StatusOf(slowFirst, "speed.shelf_first_item_p50").ShouldBe("fail");
        StatusOf(slowWhole, "speed.shelf_total_p50").ShouldBe("fail");
    }

    [Fact]
    public void A_slow_model_does_not_delay_the_first_shelf_card_because_Visions_items_show_first()
    {
        var run = Evaluate(MeetingEveryBar(p => p.PhotoId == "s1" ? p with { FirstItemMs = 5000 } : p));

        StatusOf(run, "speed.shelf_first_item_p50").ShouldBe("pass");
    }

    [Fact]
    public void A_whole_shelf_takes_Visions_read_and_then_the_models_so_two_reads_each_under_8_seconds_can_still_fail()
    {
        // 6 s for the model and 2.5 s for Vision: each is under the 8 s bar, the shelf the person waited for is 8.5 s.
        var run = Evaluate(MeetingEveryBar(changeVision: p => p.PhotoId == "s1" ? p with { LatencyMs = 2500 } : p));

        var measure = run.Summary.Gate.Measures.Single(m => m.Name == "speed.shelf_total_p50");
        measure.Actual.ShouldBe("8500 ms");
        measure.Status.ShouldBe("fail");
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
        // The combined shelf row carries the thermal state of Vision's read (the last the phone makes on a photo); a single cover keeps the model's.
        var hot = Evaluate(MeetingEveryBar(changeVision: p => p.PhotoId == "s1" ? p with { ThermalState = "serious" } : p));
        var slowing = Evaluate(MeetingEveryBar(p => p.PhotoId is "l1" or "c1" ? p with { LatencyMs = 4000 } : p));

        StatusOf(hot, "thermals").ShouldBe("fail");
        StatusOf(slowing, "thermals").ShouldBe("fail");
    }

    [Fact]
    public void Beating_the_vision_text_engine_is_reported_as_information_and_never_decides_the_verdict()
    {
        // Vision text reads everything right, so the model only ties it (the old gate failed that).
        var tie = Evaluate(MeetingEveryBar(visionReadsAllRight: true));

        // Vision text also reads the whole shelf where the model reads half of it; the combined row still has the whole shelf.
        var behind = Evaluate(MeetingEveryBar(p => p.PhotoId == "s1" ? ShelfPhoto(Apple, right: false) : p, visionReadsAllRight: true));

        foreach (var run in new[] { tie, behind })
        {
            StatusOf(run, "top1.beats_vision_text").ShouldBe(M3Gate.Info);
            run.Summary.Gate.Comparison.AppleBeatsVision.ShouldBe(false);
            run.Summary.Gate.Measures.Where(m => m.Status is not (M3Gate.Pass or M3Gate.Info)).ShouldBeEmpty();
            run.Summary.Gate.Verdict.ShouldBe(M3Gate.Pass);
        }

        tie.Summary.Gate.Measures.Single(m => m.Name == "top1.beats_vision_text").Actual.ShouldBe("100% vs 100%");
        behind.Summary.Gate.Measures.Single(m => m.Name == "top1.beats_vision_text").Actual.ShouldBe("87.5% vs 100%");
    }

    [Fact]
    public void Beating_vision_without_a_vision_engine_is_information_with_no_number_not_a_missing_measure()
    {
        var run = Evaluate(MeetingEveryBar().Where(p => p.Engine == Apple));

        var measure = run.Summary.Gate.Measures.Single(m => m.Name == "top1.beats_vision_text");
        measure.Status.ShouldBe(M3Gate.Info);
        measure.Actual.ShouldBeNull();
    }

    [Fact]
    public void A_gate_with_a_measure_that_has_no_photos_yet_is_incomplete_not_a_pass()
    {
        // No bare-label picture was read by either engine, so that kind has no photos behind it.
        var run = Evaluate(MeetingEveryBar().Where(p => p.PhotoId != "l1"));

        StatusOf(run, "top1.bare_label").ShouldBe("no_data");
        StatusOf(run, "top1.book_spine").ShouldBe("pass");
        run.Summary.Gate.Verdict.ShouldBe("incomplete");
    }

    [Fact]
    public void A_run_without_Vision_rows_has_no_combined_row_so_the_gate_has_nothing_to_measure_and_is_incomplete()
    {
        var run = Evaluate(MeetingEveryBar().Where(p => p.Engine == Apple));

        run.Summary.Engines.Keys.ShouldBe([Apple]);
        StatusOf(run, "top1.book_spine").ShouldBe("no_data");
        StatusOf(run, "speed.single_p50").ShouldBe("no_data");
        run.Summary.Gate.Verdict.ShouldBe("incomplete");
    }

    // ---- the gate reads the combined row ---------------------------------------------------------------------------------

    /// <summary>
    /// A shelf where the model adds a title that is not on it with the top tier (a wrong auto-add on its own row) and Vision reads both
    /// spines. On the combined row Vision's two items stand and the model's extra title is unconfirmed, so it is review, not an auto-add.
    /// </summary>
    private static List<GroundedPhoto> AShelfWhereOnlyTheModelAutoAddsAWrongTitle() =>
        MeetingEveryBar(
            changeApple: p => p.PhotoId == "s1" ? Photo("s1", Apple, [T1(Matched("Star Fox 64")), T1(Matched("Zorblax Quest"))], 6000, 1500) : p,
            changeVision: p => p.PhotoId == "s1" ? ShelfPhoto(Vision, right: true, latencyMs: 1500, firstItemMs: 1000) : p);

    [Fact]
    public void The_gate_takes_auto_add_precision_from_the_combined_row_not_the_model_alone()
    {
        var run = Evaluate(AShelfWhereOnlyTheModelAutoAddsAWrongTitle());

        // The model alone auto-adds 8 titles and one is wrong: under the 99% bar.
        var alone = M3Gate.Evaluate(new Dictionary<string, EngineSummary> { [Combined] = run.Summary.Engines[Apple] });
        alone.Measures.Single(m => m.Name == "auto_add_precision").Status.ShouldBe(M3Gate.Fail);

        // What the app shows auto-adds only Vision's two items and the six covers, all right: that bar passes.
        var measure = run.Summary.Gate.Measures.Single(m => m.Name == "auto_add_precision");
        measure.Status.ShouldBe(M3Gate.Pass);
        measure.Actual.ShouldBe("100%");
    }

    [Fact]
    public void A_title_only_the_model_saw_still_counts_as_invented_on_the_combined_row_because_the_person_is_shown_it()
    {
        var run = Evaluate(AShelfWhereOnlyTheModelAutoAddsAWrongTitle());

        // It goes to review instead of being added, but it is shown: one of nine shown items is invented, over the 1% bar.
        StatusOf(run, "invented_titles").ShouldBe(M3Gate.Fail);
        run.Summary.Gate.Verdict.ShouldBe(M3Gate.Fail);
    }

    [Fact]
    public void The_gate_names_the_row_it_measured_and_the_model_and_vision_rows_stay_in_the_summary_for_diagnosis()
    {
        var run = Evaluate(MeetingEveryBar());

        run.Summary.Gate.Engine.ShouldBe(Combined);
        run.Summary.Engines.Keys.ShouldBe([Apple, Combined, Vision]);
    }

    [Fact]
    public void A_single_cover_on_the_combined_row_is_the_models_own_read_whatever_Vision_read()
    {
        var run = Evaluate(MeetingEveryBar());

        // The scanner reads a cover with the model (and its cover cue); Vision's cover read, right or wrong, is not shown.
        var model = run.Summary.Engines[Apple].Single!;
        var combined = run.Summary.Engines[Combined].Single!;
        combined.Photos.ShouldBe(model.Photos);
        combined.Top1Hits.ShouldBe(model.Top1Hits);
        combined.Top3Hits.ShouldBe(model.Top3Hits);
        combined.AutoAdds.ShouldBe(model.AutoAdds);
        combined.LatencyP50Ms.ShouldBe(model.LatencyP50Ms);
        combined.ByKind.Keys.ShouldBe(model.ByKind.Keys);
    }

    [Fact]
    public void Model_timeouts_are_the_models_own_rate_because_a_shelf_timeout_is_hidden_behind_Visions_items_on_the_combined_row()
    {
        // The model times out on the shelf and Vision reads it: the scanner shows Vision's items, so the combined row is Vision's read, not a timeout.
        var run = Evaluate(MeetingEveryBar(p => p.PhotoId == "s1" ? Photo("s1", Apple, [], 8200, firstItemMs: null, outcome: "timeout", fellBackFrom: "timeout") : p));

        run.Summary.Engines[Combined].Shelf!.Timeouts.ShouldBe(0);
        run.Summary.Engines[Apple].Shelf!.Timeouts.ShouldBe(1);
        var measure = run.Summary.Gate.Measures.Single(m => m.Name == "model_timeouts");
        measure.Actual.ShouldBe("14.29%"); // one of the model's seven photos
        measure.Status.ShouldBe(M3Gate.Pass); // reported; the wait is counted in the speed measures
    }

    [Fact]
    public void A_shelf_where_the_model_timed_out_is_slow_on_the_combined_row_by_the_wait_but_its_first_card_is_still_Visions()
    {
        var run = Evaluate(MeetingEveryBar(p => p.PhotoId == "s1" ? Photo("s1", Apple, [], 8200, firstItemMs: null, outcome: "timeout", fellBackFrom: "timeout") : p));

        StatusOf(run, "speed.shelf_total_p50").ShouldBe("fail"); // 8.2 s of waiting for the model, then Vision's items
        StatusOf(run, "speed.shelf_first_item_p50").ShouldBe("pass"); // Vision's items are the first card
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
        EvalRegression.Check(new Dictionary<string, double> { ["old.top1"] = 90, ["a.top1"] = 90 }, new Dictionary<string, double> { ["new.top1"] = 10, ["a.top1"] = 90 }).Status.ShouldBe("ok");
        EvalRegression.Check(null, new Dictionary<string, double> { ["x"] = 1 }).Status.ShouldBe("no_baseline");
        EvalRegression.Check(new Dictionary<string, double>(), new Dictionary<string, double> { ["x"] = 1 }).Status.ShouldBe("no_baseline");
    }

    [Fact]
    public void A_baseline_that_shares_no_metric_with_the_run_is_no_overlap_not_a_pass()
    {
        var check = EvalRegression.Check(new Dictionary<string, double> { ["old.top1"] = 90 }, new Dictionary<string, double> { ["new.top1"] = 10 });

        check.Status.ShouldBe("no_overlap");
        check.Compared.ShouldBe(0);
        check.Regressions.ShouldBeEmpty();
    }

    [Fact]
    public void A_run_is_checked_against_the_baseline_by_the_metric_names_the_summary_publishes()
    {
        var first = Evaluate(MeetingEveryBar());
        var baseline = first.Summary.Metrics.ToDictionary(m => m.Key, m => m.Value);
        baseline["vision_pipeline.single.top1"] = 100; // as if the last run had read every single right; Vision's text engine is exact, so any fall is real

        var run = Evaluate(MeetingEveryBar(), baseline);

        run.Summary.Regression.Status.ShouldBe("regressed");
        run.Summary.Regression.Regressions.Select(r => r.Metric).ShouldContain("vision_pipeline.single.top1");
        Evaluate(MeetingEveryBar(), first.Summary.Metrics).Summary.Regression.Status.ShouldBe("ok");
    }

    [Fact]
    public void A_baseline_holds_each_engines_own_numbers_so_moving_the_gate_to_the_combined_row_changes_what_none_of_its_keys_mean()
    {
        // A baseline taken before ADR-032 has only the engines' metric keys (never a gate measure, never the combined row's): it is compared as it always was.
        var first = Evaluate(MeetingEveryBar());
        var before = first.Summary.Metrics.Where(m => !m.Key.StartsWith(Combined + ".", StringComparison.Ordinal)).ToDictionary(m => m.Key, m => m.Value);
        before["vision_pipeline.single.top1"] = 100; // as if the last run had read every single right; Vision's text engine is exact, so any fall is real

        var run = Evaluate(MeetingEveryBar(), before);

        run.Summary.Regression.Status.ShouldBe("regressed");
        run.Summary.Regression.Regressions.Select(r => r.Metric).ShouldBe(["vision_pipeline.single.top1"]);
        run.Summary.Regression.Compared.ShouldBe(before.Keys.Count(EvalRegression.IsJudged)); // the raw counts are not judged
    }

    [Fact]
    public void A_failing_gate_on_the_combined_row_does_not_touch_the_regression_check_and_a_run_compared_with_itself_is_ok()
    {
        GroundedPhoto Wrong(GroundedPhoto p) => p.PhotoId == "c1" ? OnePhoto(Singles[4], Apple, right: false) : p;
        var failing = Evaluate(MeetingEveryBar(Wrong));

        var again = Evaluate(MeetingEveryBar(Wrong), failing.Summary.Metrics);

        failing.Summary.Gate.Verdict.ShouldBe("fail");
        again.Summary.Regression.Status.ShouldBe("ok");
        again.Summary.Regression.Compared.ShouldBe(failing.Summary.Metrics.Keys.Count(EvalRegression.IsJudged)); // the raw counts are not judged
    }

    // ---- the report ------------------------------------------------------------------------------------------------------

    private const string SwitchNote = "Gate measured on the combined row since ADR-032; earlier runs measured the model alone.";

    [Fact]
    public void The_report_says_the_gate_measures_what_the_app_shows_and_carries_the_one_time_note_in_the_gate_section()
    {
        var report = Evaluate(MeetingEveryBar()).ReportMarkdown;

        var gate = report.IndexOf("## M3 gate: PASS", StringComparison.Ordinal);
        var note = report.IndexOf(SwitchNote, StringComparison.Ordinal);
        var firstEngine = report.IndexOf("## apple_model", StringComparison.Ordinal);
        gate.ShouldBeGreaterThanOrEqualTo(0);
        note.ShouldBeGreaterThan(gate);
        note.ShouldBeLessThan(firstEngine);
        var section = report[gate..firstEngine];
        section.ShouldContain("what the app shows");
        section.ShouldContain("combined row");
    }

    [Fact]
    public void The_report_keeps_a_section_for_every_engine_row_and_shows_beats_vision_as_information()
    {
        var report = Evaluate(MeetingEveryBar()).ReportMarkdown;

        report.ShouldContain("## apple_model");
        report.ShouldContain("## apple_vision");
        report.ShouldContain("## vision_pipeline");
        report.Split('\n').Single(l => l.StartsWith("| top1.beats_vision_text |", StringComparison.Ordinal)).TrimEnd().ShouldEndWith("| info |");
        report.ShouldContain("not part of the verdict");
    }
}
