using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// A shelf read only the picture model saw, at a place Vision found nothing, is kept for review by the scanner (T2 at most), however
/// sure the model was and however exactly the catalogue matched it. The scorer follows: such a read is review, never an auto-add, so
/// a model that invents a title that happens to exist cannot lower the auto-add precision.
/// </summary>
public sealed class UnconfirmedReadScoringTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private ScoredEngines ScoreShelf(params GroundedItem[] read)
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Wave Race 64")]);
        return EvalScorer.Score([Photo("s1", Apple, read)], TruthsOf(_golden));
    }

    private static GroundedItem Unconfirmed(GroundedItem item) => item with { Unconfirmed = true };

    [Fact]
    public void A_matched_T1_read_nothing_else_saw_is_review_not_an_auto_add()
    {
        var shelf = ScoreShelf(Unconfirmed(T1(Matched("Star Fox 64")))).ShelfOf();

        shelf.AutoAdds.ShouldBe(0);
        shelf.T1Auto.ShouldBe(0);
        shelf.T2Review.ShouldBe(1);
        shelf.AutoAddPrecisionPct.ShouldBeNull();
    }

    [Fact]
    public void A_matched_T0_read_nothing_else_saw_is_review_too()
    {
        var shelf = ScoreShelf(Unconfirmed(T0(Matched("Star Fox 64")))).ShelfOf();

        shelf.AutoAdds.ShouldBe(0);
        shelf.T0Auto.ShouldBe(0);
        shelf.T2Review.ShouldBe(1);
    }

    [Fact]
    public void A_wrong_title_the_model_invented_cannot_lower_the_auto_add_precision()
    {
        var shelf = ScoreShelf(T1(Matched("Star Fox 64")), Unconfirmed(T1(Matched("Mario Kart 64")))).ShelfOf();

        shelf.AutoAdds.ShouldBe(1);
        shelf.CorrectAutoAdds.ShouldBe(1);
        shelf.AutoAddPrecisionPct.ShouldBe(100);
        shelf.T2Review.ShouldBe(1);
    }

    [Fact]
    public void A_read_that_is_not_unconfirmed_is_scored_by_its_tier_as_before()
    {
        var shelf = ScoreShelf(T1(Matched("Star Fox 64")), T1(Matched("Wave Race 64"))).ShelfOf();

        shelf.AutoAdds.ShouldBe(2);
        shelf.T1Auto.ShouldBe(2);
        shelf.T2Review.ShouldBe(0);
    }

    [Fact]
    public void An_unconfirmed_read_still_counts_for_what_it_found()
    {
        var shelf = ScoreShelf(Unconfirmed(T1(Matched("Star Fox 64")))).ShelfOf();

        shelf.TruthItems.ShouldBe(2);
        shelf.PredictedItems.ShouldBe(1);
        shelf.CorrectItems.ShouldBe(1);
    }
}
