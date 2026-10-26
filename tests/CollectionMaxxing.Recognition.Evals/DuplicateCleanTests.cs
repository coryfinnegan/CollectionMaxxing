using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The duplicate re-scan test (docs/16 §17): re-scanning an owned shelf is clean only when no new item appeared without an
/// explicit Add-copy and every owned hit was answered on the card. A re-scan that hit nothing owned proves nothing.
/// </summary>
public sealed class DuplicateCleanTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private SliceMetrics ScoreShelf(params GroundedPhoto[] photos)
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        _golden.AddShelf("s2", [ShelfItem("Mario Kart 64")]);
        return EvalScorer.Score(photos, TruthsOf(_golden)).ShelfOf();
    }

    [Fact]
    public void Answered_owned_hits_with_no_unsolicited_adds_are_clean()
    {
        var shelf = ScoreShelf(
            Photo("s1", Apple, [Matched("Star Fox 64")], duplicateRescan: Rescan(owned: 3, answered: 3, unsolicited: 0)),
            Photo("s2", Apple, [Matched("Mario Kart 64")], duplicateRescan: Rescan(owned: 2, answered: 2, unsolicited: 0)));

        shelf.DuplicateClean.ShouldBe(true);
    }

    [Theory]
    [InlineData(3, 3, 1)] // one item appeared without an explicit Add-copy
    [InlineData(3, 2, 0)] // one owned hit was not answered on-card
    public void An_unsolicited_add_or_an_unanswered_hit_is_not_clean(int owned, int answered, int unsolicited)
    {
        var shelf = ScoreShelf(
            Photo("s1", Apple, [Matched("Star Fox 64")], duplicateRescan: Rescan(owned, answered, unsolicited)),
            Photo("s2", Apple, [Matched("Mario Kart 64")], duplicateRescan: Rescan(owned: 2, answered: 2, unsolicited: 0)));

        shelf.DuplicateClean.ShouldBe(false);
    }

    [Fact]
    public void A_re_scan_that_hit_nothing_owned_proves_nothing()
    {
        var shelf = ScoreShelf(Photo("s1", Apple, [Matched("Star Fox 64")], duplicateRescan: Rescan(owned: 0, answered: 0, unsolicited: 0)));

        shelf.DuplicateClean.ShouldBe(false);
    }

    [Fact]
    public void Photos_without_re_scan_evidence_leave_the_metric_without_data()
    {
        ScoreShelf(Photo("s1", Apple, [Matched("Star Fox 64")])).DuplicateClean.ShouldBeNull();
    }

    [Fact]
    public void The_flattening_names_duplicate_clean_and_a_dirty_re_scan_is_the_regression()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        var metrics = EvalRegression.Flatten(EvalScorer.Score(
            [Photo("s1", Apple, [Matched("Star Fox 64")], duplicateRescan: Rescan(owned: 1, answered: 1, unsolicited: 0))],
            TruthsOf(_golden)).Engines);

        metrics["apple_model.shelf.duplicate_clean"].ShouldBe(100);

        var baseline = new Dictionary<string, double> { ["apple_model.shelf.duplicate_clean"] = 100 };
        var dirty = metrics.ToDictionary(m => m.Key, m => m.Value);
        dirty["apple_model.shelf.duplicate_clean"] = 0;

        EvalRegression.Check(baseline, dirty).Status.ShouldBe(EvalRegression.Regressed);
    }
}
