using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The scanner shows a shelf's Vision items first and the picture model adds what Vision missed. The eval scores that as a
/// report-only engine, <c>apple_vision</c>, built on the server from the two rows the phone uploaded. Two reads are the same item
/// when their boxes overlap (so the same title on two spines is two items); with no boxes, when their best release is the same.
/// </summary>
public sealed class CombinedEngineTests : IDisposable
{
    private const string Combined = EvalCombination.Engine;

    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private IReadOnlyDictionary<string, TruthEntry> ArrangeShelf(params string[] titles)
    {
        _golden.AddShelf("s1", titles.Select(t => ShelfItem(t)));
        return TruthsOf(_golden);
    }

    /// <summary>What the Vision text engine sends: a low-confidence read, kept as a guess for a person.</summary>
    private static GroundedItem GuessedByVision(string title) => Kept(Matched(title, resolvedBy: "vision_text"));

    private static GroundedPhoto ModelRead(params GroundedItem[] items) => Photo("s1", Apple, items, latencyMs: 4000, firstItemMs: 900);

    private static GroundedPhoto VisionRead(params GroundedItem[] items) => Photo("s1", Vision, items, latencyMs: 700, firstItemMs: null);

    private static ScoredEngines Score(IReadOnlyDictionary<string, TruthEntry> truths, params GroundedPhoto[] photos) => EvalScorer.Score(photos, truths);

    // ---- what the combination counts -------------------------------------------------------------------------------------

    [Fact]
    public void A_book_only_Vision_found_counts_for_the_combination()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64", "Mario Kart 64", "Banjo-Kazooie"),
            ModelRead(T1(Matched("Star Fox 64"))),
            VisionRead(GuessedByVision("Mario Kart 64")));

        scored.ShelfOf(Apple).Top1Hits.ShouldBe(1);
        scored.ShelfOf(Vision).Top1Hits.ShouldBe(1);
        var both = scored.ShelfOf(Combined);
        both.TruthItems.ShouldBe(3);
        both.Top1Hits.ShouldBe(2);
        both.CorrectItems.ShouldBe(2);
        both.PredictedItems.ShouldBe(2);
        both.RecallPct.ShouldBe(66.67);
    }

    [Fact]
    public void A_book_both_engines_found_counts_once()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64", "Mario Kart 64"),
            ModelRead(T1(Matched("Star Fox 64")), T1(Matched("Mario Kart 64"))),
            VisionRead(GuessedByVision("Star Fox 64"), GuessedByVision("Mario Kart 64")));

        var both = scored.ShelfOf(Combined);
        both.Top1Hits.ShouldBe(2);
        both.CorrectItems.ShouldBe(2);
        both.PredictedItems.ShouldBe(2);
        both.ProvisionalItems.ShouldBe(2); // Vision's reads stand where both found it, and Vision's are guesses
        both.PrecisionPct.ShouldBe(100);
    }

    [Fact]
    public void The_same_book_is_found_by_its_normalised_title_however_each_engine_spelled_it()
    {
        var scored = Score(
            ArrangeShelf("The Legend of Zelda"),
            ModelRead(T1(Matched("The Legend of Zelda"))),
            VisionRead(GuessedByVision("Legend of Zelda, The")));

        scored.ShelfOf(Combined).PredictedItems.ShouldBe(1);
    }

    [Fact]
    public void The_same_title_in_another_year_is_another_release_and_is_added()
    {
        var model = T1(Matched("Star Fox 64"));
        var visionRelease = Kept(new GroundedItem("star fox", null, "vision_text", "matched", [Release("Star Fox 64") with { Year = 2011 }]));

        var scored = Score(ArrangeShelf("Star Fox 64"), ModelRead(model), VisionRead(visionRelease));

        scored.ShelfOf(Combined).PredictedItems.ShouldBe(2);
        scored.ShelfOf(Combined).Top1Hits.ShouldBe(1);
    }

    [Fact]
    public void Every_Vision_item_is_kept_and_a_model_item_with_no_release_is_added_because_nothing_shows_Vision_read_it()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            ModelRead(T1(Matched("Star Fox 64")), NotFound("smudged")),
            VisionRead(Kept(NotFound("also smudged")), GuessedByVision("Star Fox 64")));

        scored.ShelfOf(Combined).PredictedItems.ShouldBe(3); // Vision's two, and the model's smudged one; its Star Fox is Vision's
        scored.ShelfOf(Combined).Top1Hits.ShouldBe(1);
        scored.ShelfOf(Vision).PredictedItems.ShouldBe(2);
    }

    [Fact]
    public void A_book_found_only_by_Vision_stays_a_provisional_guess_and_is_never_an_auto_add()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64", "Mario Kart 64"),
            ModelRead(T1(Matched("Star Fox 64"))),
            VisionRead(GuessedByVision("Mario Kart 64")));

        var both = scored.ShelfOf(Combined);
        both.ProvisionalItems.ShouldBe(1);
        both.AutoAdds.ShouldBe(1);
        both.CorrectAutoAdds.ShouldBe(1);
        both.T1Auto.ShouldBe(1);
        both.ResolvedBy["vision_text"].ShouldBe(1);
        both.ResolvedBy["model"].ShouldBe(1);
        both.ReviewRatePct.ShouldBe(50); // the Vision book still needs a person's touch
    }

    [Fact]
    public void A_wrong_Vision_guess_is_counted_against_the_combination_but_not_the_model()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            ModelRead(T1(Matched("Star Fox 64"))),
            VisionRead(GuessedByVision("Zorblax Quest")));

        scored.ShelfOf(Apple).InventedTitles.ShouldBe(0);
        var both = scored.ShelfOf(Combined);
        both.InventedTitles.ShouldBe(1);
        both.PrecisionPct.ShouldBe(50);
    }

    // ---- time, outcome and the rows it needs -----------------------------------------------------------------------------

    [Fact]
    public void The_combined_read_takes_Visions_time_plus_the_models_and_shows_its_first_item_when_Vision_does()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            ModelRead(T1(Matched("Star Fox 64"))),
            Photo("s1", Vision, [GuessedByVision("Star Fox 64")], latencyMs: 700, firstItemMs: 300));

        var both = scored.ShelfOf(Combined);
        both.LatencyP50Ms.ShouldBe(4700, "Vision reads and then the model does, so the total is both");
        both.FirstItemP50Ms.ShouldBe(300, "Vision's items show first, so the first item is Vision's");
        scored.ShelfOf(Apple).LatencyP50Ms.ShouldBe(4000);
        scored.ShelfOf(Apple).FirstItemP50Ms.ShouldBe(900);
    }

    [Fact]
    public void When_Vision_sent_no_first_item_time_the_first_item_is_the_end_of_its_whole_read()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            ModelRead(T1(Matched("Star Fox 64"))),
            VisionRead(GuessedByVision("Star Fox 64")));

        scored.ShelfOf(Combined).FirstItemP50Ms.ShouldBe(700);
    }

    [Fact]
    public void When_Vision_found_nothing_the_first_item_is_the_models_after_Visions_whole_read()
    {
        var scored = Score(ArrangeShelf("Star Fox 64"), ModelRead(T1(Matched("Star Fox 64"))), VisionRead());

        var both = scored.ShelfOf(Combined);
        both.FirstItemP50Ms.ShouldBe(1600); // Vision's 700 and then the model's first item at 900
        both.LatencyP50Ms.ShouldBe(4700);
        both.PredictedItems.ShouldBe(1);
    }

    [Fact]
    public void When_Vision_failed_the_first_item_time_is_the_models_own()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            ModelRead(T1(Matched("Star Fox 64"))),
            Photo("s1", Vision, [], latencyMs: 300, firstItemMs: 100, outcome: "error"));

        scored.ShelfOf(Combined).FirstItemP50Ms.ShouldBe(900);
    }

    [Fact]
    public void The_outcome_is_the_models_when_both_engines_failed_or_found_nothing_and_the_row_is_empty()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            Photo("s1", Apple, [], latencyMs: 8200, firstItemMs: null, outcome: "timeout", fellBackFrom: "timeout"),
            Photo("s1", Vision, [], latencyMs: 300, firstItemMs: null, outcome: "error"));

        var both = scored.ShelfOf(Combined);
        both.Timeouts.ShouldBe(1);
        both.Fallbacks.ShouldBe(1);
        both.PredictedItems.ShouldBe(0);
        both.Top1Hits.ShouldBe(0);
    }

    [Fact]
    public void A_Vision_read_that_failed_leaves_the_models_reading_as_it_was()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            ModelRead(T1(Matched("Star Fox 64"))),
            Photo("s1", Vision, [], latencyMs: 300, outcome: "error"));

        var both = scored.ShelfOf(Combined);
        both.PredictedItems.ShouldBe(1);
        both.Top1Hits.ShouldBe(1);
        both.Failures.ShouldBe(0);
    }

    [Fact]
    public void Segments_the_phone_counted_still_reconcile_when_Vision_adds_items()
    {
        var capturedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
        var model = Photo("s1", Apple, [T1(Matched("Star Fox 64")) with { SegmentId = "m" }], capturedCount: 1, capturedAt: capturedAt);
        var vision = Photo("s1", Vision, [GuessedByVision("Mario Kart 64") with { SegmentId = "v" }], capturedCount: 1, capturedAt: capturedAt);

        var scored = Score(ArrangeShelf("Star Fox 64", "Mario Kart 64"), model, vision);

        scored.ShelfOf(Combined).UnreconciledPhotos.ShouldBe(0);
        scored.ShelfOf(Combined).Top1Hits.ShouldBe(2);
    }

    [Fact]
    public void A_photo_without_both_rows_gets_no_combined_row()
    {
        var truths = ArrangeShelf("Star Fox 64");

        Score(truths, ModelRead(T1(Matched("Star Fox 64")))).Engines.Keys.ShouldBe([Apple]);
        Score(truths, VisionRead(GuessedByVision("Star Fox 64"))).Engines.Keys.ShouldBe([Vision]);
    }

    [Fact]
    public void A_photo_with_no_truth_file_is_not_scored_for_the_combination_either()
    {
        _golden.AddShelf("s1", null);

        var scored = Score(TruthsOf(_golden), ModelRead(T1(Matched("Star Fox 64"))), VisionRead(GuessedByVision("Star Fox 64")));

        scored.Engines.ShouldBeEmpty();
    }

    // ---- when the model fails, Vision's reading stands (as it does in the scanner) --------------------------------------------

    // The twelfth-run photo accept-shelf-04: the model timed out with no items, Vision found 41 and scored 23 of 46, and the
    // combined row scored 0 of 46. The scanner keeps Vision's reading when the model fails on a shelf; so must the combination.
    private static GroundedPhoto ModelFailed(string outcome = "timeout", string? fellBackFrom = "timeout") =>
        Photo("s1", Apple, [], latencyMs: 8200, firstItemMs: null, outcome: outcome, fellBackFrom: fellBackFrom);

    [Fact]
    public void A_model_timeout_leaves_Visions_items_as_the_combined_read_and_they_are_scored()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64", "Mario Kart 64", "Banjo-Kazooie"),
            ModelFailed(),
            Photo("s1", Vision, [GuessedByVision("Star Fox 64"), GuessedByVision("Mario Kart 64")], latencyMs: 700, firstItemMs: 300));

        scored.ShelfOf(Apple).PredictedItems.ShouldBe(0);
        scored.ShelfOf(Vision).Top1Hits.ShouldBe(2);
        var both = scored.ShelfOf(Combined);
        both.TruthItems.ShouldBe(3);
        both.PredictedItems.ShouldBe(2);
        both.Top1Hits.ShouldBe(2);
        both.CorrectItems.ShouldBe(2);
        both.RecallPct.ShouldBe(66.67);
    }

    [Fact]
    public void The_combined_read_after_a_model_failure_says_so_with_a_fallback_and_is_not_a_timeout_or_a_failure_of_its_own()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            ModelFailed(),
            VisionRead(GuessedByVision("Star Fox 64")));

        var both = scored.ShelfOf(Combined);
        both.Fallbacks.ShouldBe(1);
        both.Timeouts.ShouldBe(0, "Vision's reading is the snap and it answered; the timeout is the model row's to report");
        both.Failures.ShouldBe(0);
        both.Refusals.ShouldBe(0);
        scored.ShelfOf(Apple).Timeouts.ShouldBe(1);
    }

    [Fact]
    public void The_combined_read_after_a_model_failure_takes_Visions_time_and_shows_its_first_item_when_Vision_does()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            ModelFailed(),
            Photo("s1", Vision, [GuessedByVision("Star Fox 64")], latencyMs: 700, firstItemMs: 300));

        var both = scored.ShelfOf(Combined);
        both.LatencyP50Ms.ShouldBe(700);
        both.FirstItemP50Ms.ShouldBe(300);
    }

    [Fact]
    public void When_Vision_sent_no_first_item_time_after_a_model_failure_the_first_item_is_the_end_of_its_whole_read()
    {
        var scored = Score(ArrangeShelf("Star Fox 64"), ModelFailed(), VisionRead(GuessedByVision("Star Fox 64")));

        scored.ShelfOf(Combined).FirstItemP50Ms.ShouldBe(700);
    }

    [Theory]
    [InlineData("timeout", null, "timeout")]
    [InlineData("refused", null, "declined")]
    [InlineData("error", null, "unavailable")]
    [InlineData("error", "context", "context")]
    [InlineData("refused", "declined", "declined")]
    [InlineData("timeout", "unavailable", "unavailable")]
    public void The_reason_the_model_failed_is_the_one_the_phone_sent_else_what_its_outcome_says(string outcome, string? sent, string expected)
    {
        var built = EvalCombination.Of(
            [ModelFailed(outcome, sent), VisionRead(GuessedByVision("Star Fox 64"))],
            ArrangeShelf("Star Fox 64")).Single();

        built.Engine.ShouldBe(Combined);
        built.Outcome.ShouldBe("ok");
        built.FellBackFrom.ShouldBe(expected);
        built.Items.Select(i => i.ResolvedBy).ShouldBe(["vision_text"]);
        built.Items[0].Provisional.ShouldBeTrue();
    }

    [Fact]
    public void After_a_model_failure_Visions_own_counts_stand_so_its_segments_still_reconcile()
    {
        var capturedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
        var vision = Photo(
            "s1", Vision,
            [GuessedByVision("Star Fox 64") with { SegmentId = "a" }, GuessedByVision("Mario Kart 64") with { SegmentId = "b" }],
            capturedCount: 3, unreadable: 1, capturedAt: capturedAt);

        var scored = Score(ArrangeShelf("Star Fox 64", "Mario Kart 64", "Banjo-Kazooie"), ModelFailed(), vision);

        var both = scored.ShelfOf(Combined);
        both.UnreconciledPhotos.ShouldBe(0, "Vision's two segments and its one unreadable are the three it captured");
        both.Top1Hits.ShouldBe(2);
    }

    [Fact]
    public void A_model_that_answered_with_nothing_leaves_Visions_items_and_no_fallback()
    {
        var built = EvalCombination.Of(
            [Photo("s1", Apple, [], latencyMs: 4000), VisionRead(GuessedByVision("Star Fox 64"))],
            ArrangeShelf("Star Fox 64")).Single();

        built.Outcome.ShouldBe("ok");
        built.FellBackFrom.ShouldBeNull("the model answered; it just found nothing");
        built.Items.Select(i => i.ResolvedBy).ShouldBe(["vision_text"]);
    }

    [Fact]
    public void A_Vision_failure_with_a_model_that_has_items_gives_the_models_items()
    {
        var built = EvalCombination.Of(
            [ModelRead(T1(Matched("Star Fox 64"))), Photo("s1", Vision, [], latencyMs: 300, outcome: "error")],
            ArrangeShelf("Star Fox 64")).Single();

        built.Engine.ShouldBe(Combined);
        built.Outcome.ShouldBe("ok");
        built.FellBackFrom.ShouldBeNull();
        built.Items.Select(i => i.Best!.Title).ShouldBe(["Star Fox 64"]);
        built.Items.Select(i => i.ResolvedBy).ShouldBe(["model"]);
    }

    [Fact]
    public void Both_engines_failing_gives_an_empty_row_that_is_the_models_failure()
    {
        var built = EvalCombination.Of(
            [ModelFailed(), Photo("s1", Vision, [], latencyMs: 300, outcome: "error")],
            ArrangeShelf("Star Fox 64")).Single();

        built.Engine.ShouldBe(Combined);
        built.Items.ShouldBeEmpty();
        built.Outcome.ShouldBe("timeout");
    }

    [Fact]
    public void A_model_failure_where_Vision_found_nothing_is_still_the_models_failure_and_an_empty_row()
    {
        var built = EvalCombination.Of([ModelFailed(), VisionRead()], ArrangeShelf("Star Fox 64")).Single();

        built.Items.ShouldBeEmpty();
        built.Outcome.ShouldBe("timeout");
        built.FellBackFrom.ShouldBe("timeout");
    }

    // ---- a cover shot is not combined ------------------------------------------------------------------------------------

    [Fact]
    public void A_single_picture_the_model_timed_out_on_does_not_borrow_Visions_items()
    {
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", EvalKinds.LooseCartridge, "Nintendo 64"));
        var model = Photo("c1", Apple, [], latencyMs: 8200, outcome: "timeout", fellBackFrom: "timeout");
        var vision = Photo("c1", Vision, [GuessedByVision("Star Fox 64")], latencyMs: 700);

        var scored = EvalScorer.Score([model, vision], TruthsOf(_golden));

        var both = scored.SoloOf(Combined);
        both.PredictedItems.ShouldBe(0);
        both.Timeouts.ShouldBe(1);
    }

    [Fact]
    public void A_single_item_picture_is_the_models_own_reading_because_the_scanner_does_not_combine_a_cover_shot()
    {
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", EvalKinds.LooseCartridge, "Nintendo 64"));
        var model = Photo("c1", Apple, [T1(Matched("Star Fox 64"))], latencyMs: 2000);
        var vision = Photo("c1", Vision, [GuessedByVision("Zorblax Quest")], latencyMs: 700);

        var scored = EvalScorer.Score([model, vision], TruthsOf(_golden));

        var both = scored.SoloOf(Combined);
        both.PredictedItems.ShouldBe(1);
        both.InventedTitles.ShouldBe(0);
        both.LatencyP50Ms.ShouldBe(2000);
    }

    [Fact]
    public void A_shelf_picture_labelled_a_single_cover_is_not_combined_either()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64")], shotKind: "single_cover");

        var scored = Score(
            TruthsOf(_golden),
            ModelRead(T1(Matched("Star Fox 64"))),
            VisionRead(GuessedByVision("Mario Kart 64")));

        scored.ShelfOf(Combined).Top1Hits.ShouldBe(1);
        scored.ShelfOf(Combined).LatencyP50Ms.ShouldBe(4000);
    }

    // ---- what it must not move -------------------------------------------------------------------------------------------

    private static List<GroundedPhoto> ARunWhereVisionFindsWhatTheModelMissed() =>
    [
        Photo("s1", Apple, [T1(Matched("Star Fox 64"))], latencyMs: 6000, firstItemMs: 1500),
        Photo("s1", Vision, [GuessedByVision("Mario Kart 64")], latencyMs: 700, firstItemMs: null),
    ];

    private EvaluatedRun Evaluate(IEnumerable<GroundedPhoto> photos, IReadOnlyDictionary<string, double>? baseline = null)
    {
        var truths = ArrangeShelf("Star Fox 64", "Mario Kart 64");
        return EvalReport.Evaluate(RunOf([.. photos]), truths, baseline, "abc1234", []);
    }

    [Fact]
    public void The_combined_engine_appears_in_the_summary_next_to_the_others_and_in_the_report()
    {
        var run = Evaluate(ARunWhereVisionFindsWhatTheModelMissed());

        run.Summary.Engines.Keys.ShouldBe([Apple, Combined, Vision]);
        run.Summary.Metrics.Keys.ShouldContain($"{Combined}.shelf.top1");
        run.Summary.Metrics[$"{Combined}.shelf.top1"].ShouldBe(100);
        run.ReportMarkdown.ShouldContain($"## {Combined}");
        run.ReportMarkdown.ShouldContain("Report only");
        run.ReportMarkdown.ShouldContain("the Vision text items first, then the Apple model's items Vision did not read");
        run.ReportMarkdown.ShouldContain("the first item is Vision's");
        run.Summary.ScoredPhotos.ShouldBe(2); // the rows the phone uploaded, not the one built from them
    }

    [Fact]
    public void Adding_the_combined_engine_does_not_change_the_gate_rows_or_verdict()
    {
        var run = Evaluate(ARunWhereVisionFindsWhatTheModelMissed());

        var withoutIt = run.Summary.Engines.Where(e => e.Key != Combined).ToDictionary(e => e.Key, e => e.Value);
        var gate = M3Gate.Evaluate(withoutIt);

        run.Summary.Gate.Verdict.ShouldBe(gate.Verdict);
        run.Summary.Gate.Measures.ShouldBe(gate.Measures);
        run.Summary.Gate.Comparison.ShouldBe(gate.Comparison);
        run.Summary.Gate.Engine.ShouldBe(Apple);
    }

    [Fact]
    public void The_gate_verdict_is_the_same_whatever_Vision_adds_to_the_combination()
    {
        var truths = ArrangeShelf("Star Fox 64", "Mario Kart 64");
        GroundedPhoto[] visionAddsNothing = [ModelRead(T1(Matched("Star Fox 64"))), VisionRead(GuessedByVision("Star Fox 64"))];
        GroundedPhoto[] visionAddsABook = [ModelRead(T1(Matched("Star Fox 64"))), VisionRead(GuessedByVision("Mario Kart 64"))];

        var plain = EvalReport.Evaluate(RunOf(visionAddsNothing), truths, null, "abc1234", []).Summary.Gate;
        var richer = EvalReport.Evaluate(RunOf(visionAddsABook), truths, null, "abc1234", []).Summary.Gate;

        // Only the Vision engine's own row differs between the two runs, as before; the combination feeds nothing into the gate.
        richer.Measures.Select(m => m.Name).ShouldBe(plain.Measures.Select(m => m.Name));
        richer.Measures.Where(m => m.Name != "top1.beats_vision_text").ShouldBe(plain.Measures.Where(m => m.Name != "top1.beats_vision_text"));
        richer.Verdict.ShouldBe(plain.Verdict);
    }

    [Fact]
    public void The_metrics_of_the_three_real_engines_are_what_they_were_without_the_combination()
    {
        var run = Evaluate(ARunWhereVisionFindsWhatTheModelMissed());

        var real = run.Summary.Engines.Where(e => e.Key != Combined).ToDictionary(e => e.Key, e => e.Value);
        var expected = EvalRegression.Flatten(real);
        var actual = run.Summary.Metrics.Where(m => !m.Key.StartsWith(Combined + ".", StringComparison.Ordinal)).ToDictionary(m => m.Key, m => m.Value);

        actual.ShouldBe(expected);
        run.Summary.Metrics.Keys.Where(k => k.StartsWith(Combined + ".", StringComparison.Ordinal)).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_baseline_that_knows_nothing_of_the_combination_does_not_flag_it_as_a_regression()
    {
        var first = Evaluate(ARunWhereVisionFindsWhatTheModelMissed());
        var baseline = first.Summary.Metrics.Where(m => !m.Key.StartsWith(Combined + ".", StringComparison.Ordinal)).ToDictionary(m => m.Key, m => m.Value);

        var run = Evaluate(ARunWhereVisionFindsWhatTheModelMissed(), baseline);

        run.Summary.Regression.Status.ShouldBe("ok");
        run.Summary.Regression.Compared.ShouldBe(baseline.Count);
    }

    [Fact]
    public void The_rows_the_phone_uploaded_are_not_changed_by_building_the_combination()
    {
        var photos = ARunWhereVisionFindsWhatTheModelMissed();
        var before = photos.Select(p => (p.Engine, p.Items.Count, p.LatencyMs)).ToList();

        var built = EvalCombination.Of(photos, ArrangeShelf("Star Fox 64", "Mario Kart 64"));

        photos.Select(p => (p.Engine, p.Items.Count, p.LatencyMs)).ShouldBe(before);
        built.Count.ShouldBe(1);
        built[0].Engine.ShouldBe(Combined);
        built[0].Items.Count.ShouldBe(2);
        built[0].Items.Select(i => i.ResolvedBy).ShouldBe(["vision_text", "model"]); // Vision's items first
        built[0].Items[0].IsGuess.ShouldBe(true);
        built[0].Items[0].Provisional.ShouldBeTrue();
    }

    [Fact]
    public void Vision_wins_where_both_found_a_book_so_its_read_is_the_one_shown()
    {
        var built = EvalCombination.Of(
            [ModelRead(T2(Matched("Star Fox 64"))), VisionRead(GuessedByVision("Star Fox 64"))],
            ArrangeShelf("Star Fox 64"));

        var item = built.Single().Items.Single();
        item.ResolvedBy.ShouldBe("vision_text");
        item.Provisional.ShouldBeTrue();
        item.Tier.ShouldBeNull();
    }

    // ---- where a read sits decides what the same item is -----------------------------------------------------------------

    [Fact]
    public void Vision_items_come_first_and_a_model_item_at_a_new_position_is_added_after_them()
    {
        var built = EvalCombination.Of(
            [
                ModelRead(T1(Matched("Mario Kart 64")).At(0.60)),
                VisionRead(GuessedByVision("Star Fox 64").At(0.10)),
            ],
            ArrangeShelf("Star Fox 64", "Mario Kart 64"));

        built.Single().Items.Select(i => i.Best!.Title).ShouldBe(["Star Fox 64", "Mario Kart 64"]);
        built.Single().Items.Select(i => i.ResolvedBy).ShouldBe(["vision_text", "model"]);
    }

    [Fact]
    public void A_model_item_whose_box_overlaps_a_Vision_item_is_the_same_item_and_is_not_added_whatever_it_read()
    {
        var built = EvalCombination.Of(
            [
                ModelRead(T1(Matched("Mario Kart 64")).At(0.31)),
                VisionRead(GuessedByVision("Star Fox 64").At(0.30)),
            ],
            ArrangeShelf("Star Fox 64"));

        built.Single().Items.Select(i => i.Best!.Title).ShouldBe(["Star Fox 64"]);
    }

    [Fact]
    public void The_same_title_on_two_spines_is_two_items_when_both_engines_report_where_each_sat()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64", "Star Fox 64"),
            ModelRead(T1(Matched("Star Fox 64")).At(0.60)),
            VisionRead(GuessedByVision("Star Fox 64").At(0.10)));

        var both = scored.ShelfOf(Combined);
        both.PredictedItems.ShouldBe(2);
        both.Top1Hits.ShouldBe(2);
    }

    [Fact]
    public void The_same_title_in_the_same_place_is_one_item()
    {
        var scored = Score(
            ArrangeShelf("Star Fox 64"),
            ModelRead(T1(Matched("Star Fox 64")).At(0.30)),
            VisionRead(GuessedByVision("Star Fox 64").At(0.31)));

        scored.ShelfOf(Combined).PredictedItems.ShouldBe(1);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void With_a_box_missing_on_either_side_the_title_rule_decides(bool modelHasBox, bool visionHasBox)
    {
        var model = T1(Matched("Star Fox 64"));
        var vision = GuessedByVision("Star Fox 64");
        var otherModel = T1(Matched("Mario Kart 64"));

        var sameTitle = EvalCombination.Of(
            [ModelRead(modelHasBox ? model.At(0.60) : model), VisionRead(visionHasBox ? vision.At(0.10) : vision)],
            ArrangeShelf("Star Fox 64"));
        var differentTitle = EvalCombination.Of(
            [ModelRead(modelHasBox ? otherModel.At(0.60) : otherModel), VisionRead(visionHasBox ? vision.At(0.10) : vision)],
            ArrangeShelf("Star Fox 64", "Mario Kart 64"));

        sameTitle.Single().Items.Count.ShouldBe(1, "the same release with a box missing is one item, as it was before boxes");
        differentTitle.Single().Items.Count.ShouldBe(2);
    }

    [Fact]
    public void Captured_segments_still_reconcile_when_two_spines_share_a_title()
    {
        var capturedAt = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
        var model = Photo("s1", Apple, [T1(Matched("Star Fox 64")).At(0.60) with { SegmentId = "m" }], capturedCount: 1, capturedAt: capturedAt);
        var vision = Photo("s1", Vision, [GuessedByVision("Star Fox 64").At(0.10) with { SegmentId = "v" }], capturedCount: 1, capturedAt: capturedAt);

        var scored = Score(ArrangeShelf("Star Fox 64", "Star Fox 64"), model, vision);

        scored.ShelfOf(Combined).UnreconciledPhotos.ShouldBe(0);
    }

    private static string EnumTextOf(ScanTier tier) => CollectionMaxxing.Domain.Common.EnumText.ToText(tier);
}
