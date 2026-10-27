// LOCKED tests for the honest eval harness (issue #347, docs/16 §17): the denominator is every captured segment,
// unreadable reconciles, a provisional without IsGuess is invented, partial runs never pass, and a threshold move
// without a baseline note is incomplete. Do not edit or weaken: add new tests instead.
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

public sealed class AntiGamingLockedTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private ScoredEngines ScoreShelf(GroundedPhoto photo, int truthUnreadable = 0)
    {
        _golden.AddShelf(
            photo.PhotoId,
            [ShelfItem("Star Fox 64"), ShelfItem("Mario Kart 64")],
            unreadable: truthUnreadable);
        return EvalScorer.Score([photo], TruthsOf(_golden));
    }

    // ---- the denominator is every captured segment ----------------------------------------------------------------

    [Fact]
    public void Captured_segments_that_are_neither_read_nor_unreadable_count_as_invented()
    {
        // The phone captured 4 segments but reported 2 reads and 0 unreadable: 2 segments vanished.
        var scored = ScoreShelf(Photo("s1", Apple, [Matched("Star Fox 64"), Matched("Mario Kart 64")], capturedCount: 4));
        var shelf = scored.ShelfOf();

        shelf.UnreconciledPhotos.ShouldBe(1);
        shelf.InventedTitles.ShouldBe(2); // the gap cannot hide: it lands in invented titles and precision
        shelf.PredictedItems.ShouldBe(4);
        shelf.PrecisionPct.ShouldBe(50);
        scored.Misses.ShouldContain(m => m.Kind == "unreconciled");
    }

    [Fact]
    public void Reporting_more_segments_than_were_captured_is_also_unreconciled()
    {
        var shelf = ScoreShelf(Photo("s1", Apple, [Matched("Star Fox 64"), Matched("Mario Kart 64")], capturedCount: 1)).ShelfOf();

        shelf.UnreconciledPhotos.ShouldBe(1);
        shelf.InventedTitles.ShouldBe(1);
    }

    [Fact]
    public void Reads_of_one_segment_reconcile_as_one_segment_and_unreadable_counts_too()
    {
        var both = Matched("Star Fox 64");
        var shelf = ScoreShelf(Photo(
            "s1",
            Apple,
            [both with { SegmentId = "seg-1" }, Matched("Mario Kart 64") with { SegmentId = "seg-1" }],
            unreadable: 1,
            capturedCount: 2)).ShelfOf(); // 1 segment read twice + 1 unreadable == 2 captured

        shelf.UnreconciledPhotos.ShouldBe(0);
        shelf.InventedTitles.ShouldBe(0);
    }

    [Fact]
    public void A_photo_that_never_reports_its_capture_is_not_penalised()
    {
        var shelf = ScoreShelf(Photo("s1", Apple, [Matched("Star Fox 64")])).ShelfOf();

        shelf.UnreconciledPhotos.ShouldBe(0);
        shelf.InventedTitles.ShouldBe(0);
    }

    // ---- unreadable reconciles against the truth -------------------------------------------------------------------

    [Fact]
    public void Unreadable_beyond_what_the_truth_allows_costs_recall()
    {
        // Truth says 1 unreadable; the phone claims 3, hiding 2 readable spines behind "unreadable".
        var shelf = ScoreShelf(
            Photo("s1", Apple, [Matched("Star Fox 64"), Matched("Mario Kart 64")], unreadable: 3),
            truthUnreadable: 1).ShelfOf();

        shelf.ExcessUnreadable.ShouldBe(2);
        shelf.RecallPct.ShouldBe(50); // 2 of 2 + 2 hidden
        shelf.Top1Pct.ShouldBe(50);
        shelf.PrecisionPct.ShouldBe(100); // unreadable was never a prediction
    }

    [Fact]
    public void Unreadable_within_what_the_truth_allows_costs_nothing()
    {
        var shelf = ScoreShelf(
            Photo("s1", Apple, [Matched("Star Fox 64"), Matched("Mario Kart 64")], unreadable: 1),
            truthUnreadable: 1).ShelfOf();

        shelf.ExcessUnreadable.ShouldBe(0);
        shelf.RecallPct.ShouldBe(100);
    }

    // ---- a provisional without IsGuess is invented ------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_provisional_that_does_not_say_it_is_a_guess_scores_as_invented(bool? isGuess)
    {
        var shelf = ScoreShelf(Photo("s1", Apple, [Kept(Matched("Star Fox 64"), isGuess), Matched("Mario Kart 64")])).ShelfOf();

        shelf.InventedTitles.ShouldBe(1);
        shelf.ProvisionalItems.ShouldBe(1);
    }

    [Fact]
    public void A_provisional_marked_as_a_guess_is_not_invented_but_still_needs_a_touch()
    {
        var shelf = ScoreShelf(Photo("s1", Apple, [Kept(T1(Matched("Star Fox 64"))), T1(Matched("Mario Kart 64"))])).ShelfOf();

        shelf.InventedTitles.ShouldBe(0);
        shelf.ReviewRatePct.ShouldBe(50);
        shelf.AutoAdds.ShouldBe(1); // the provisional is not an auto-add
    }

    // ---- untiered reads fail closed ----------------------------------------------------------------------------------

    [Fact]
    public void Reads_without_a_tier_all_need_a_touch_and_none_is_an_auto_add()
    {
        var shelf = ScoreShelf(Photo("s1", Apple, [Matched("Star Fox 64"), Matched("Mario Kart 64")])).ShelfOf();

        shelf.ReviewRatePct.ShouldBe(100);
        shelf.AutoAdds.ShouldBe(0);
        shelf.AutoAddPrecisionPct.ShouldBeNull();
        shelf.T0Auto.ShouldBe(0);
        shelf.T1Auto.ShouldBe(0);
        shelf.T2Review.ShouldBe(0);
    }

    // ---- partial runs never pass, whatever the new metrics say -------------------------------------------------------

    [Fact]
    public void A_partial_run_with_perfect_new_metrics_is_still_incomplete_and_never_compared()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        var photo = Photo(
            "s1", Apple, [T1(Matched("Star Fox 64"))],
            capturedCount: 1, capturedAt: new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
            duplicateRescan: Rescan(owned: 1, answered: 1, unsolicited: 0));
        var truths = TruthsOf(_golden);
        var baseline = new Dictionary<string, double> { ["apple_model.shelf.top1"] = 0 };
        var evaluated = EvalReport.Evaluate(RunOf(photo) with { Partial = true }, truths, baseline, "abc1234", []);

        var shelf = evaluated.Summary.Engines[Apple].Shelf!;
        shelf.ReviewRatePct.ShouldBe(0);
        shelf.AutoAddPrecisionPct.ShouldBe(100);
        shelf.DuplicateClean.ShouldBe(true);
        evaluated.Summary.Gate.Verdict.ShouldBe(M3Gate.Incomplete);
        evaluated.Summary.Regression.Status.ShouldBe(EvalRegression.PartialRun);
    }

    // ---- thresholds are hashed into the run ---------------------------------------------------------------------------

    [Fact]
    public void The_fingerprint_is_stable_hex_and_moves_when_any_threshold_moves()
    {
        var first = EvalThresholds.Fingerprint(new ScoringThresholds());
        var again = EvalThresholds.Fingerprint(new ScoringThresholds());

        first.ShouldBe(again);
        first.Length.ShouldBe(EvalThresholds.HashLength);
        first.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f').ShouldBeTrue();
        EvalThresholds.Fingerprint(new ScoringThresholds { MatchedScore = 0.91 }).ShouldNotBe(first);
        EvalThresholds.Fingerprint(new ScoringThresholds { AmbiguousScore = 0.61 }).ShouldNotBe(first);
    }

    [Fact]
    public void The_fingerprint_of_the_doc_defaults_is_pinned()
    {
        // A threshold move breaks this on purpose: update the hash, the baseline and the note in the same PR (docs/16 §17).
        // #452 moved autocommit.default_tiers from T0 to none.
        EvalThresholds.Fingerprint(new ScoringThresholds()).ShouldBe("129638999700db1e");
    }

    [Fact]
    public void A_threshold_move_without_a_baseline_note_is_incomplete_not_compared()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        var truths = TruthsOf(_golden);
        var baseline = new Dictionary<string, double> { ["apple_model.shelf.top1"] = 100 };
        var runHash = EvalThresholds.Fingerprint(new ScoringThresholds());

        var moved = EvalReport.Evaluate(
            RunOf(Photo("s1", Apple, [Matched("Star Fox 64")])),
            truths, baseline, "abc1234", [],
            baselineThresholdHash: "ffffffffffffffff");

        moved.Summary.ThresholdHash.ShouldBe(runHash);
        moved.Summary.Regression.Status.ShouldBe(EvalRegression.Incomplete);
        moved.Summary.Regression.Compared.ShouldBe(0);
        moved.ReportMarkdown.ShouldContain("baseline note");
    }

    [Fact]
    public void A_matching_hash_or_a_note_runs_the_comparison_as_normal()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        var truths = TruthsOf(_golden);
        var baseline = new Dictionary<string, double> { ["apple_model.shelf.top1"] = 100 };
        var runHash = EvalThresholds.Fingerprint(new ScoringThresholds());
        var photos = new[] { Photo("s1", Apple, [Matched("Star Fox 64")]) };

        EvalReport.Evaluate(RunOf(photos), truths, baseline, "abc1234", [], baselineThresholdHash: runHash)
            .Summary.Regression.Status.ShouldBe(EvalRegression.Ok);
        EvalReport.Evaluate(RunOf(photos), truths, baseline, "abc1234", [], baselineThresholdHash: "ffffffffffffffff")
            .Summary.Regression.Status.ShouldBe(EvalRegression.Incomplete);
        EvalReport.Evaluate(RunOf(photos) with { BaselineNote = "T1 bar 0.90 -> 0.91, acceptance re-run" }, truths, baseline, "abc1234", [],
                baselineThresholdHash: "ffffffffffffffff")
            .Summary.Regression.Status.ShouldBe(EvalRegression.Ok);
    }
}
