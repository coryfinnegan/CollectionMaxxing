using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The second shelf run (#509). The phone tiers every confident read T1, so reads the server only narrowed down, or found
/// nothing for, scored as wrong auto-adds that the server would never have made. And every miss of a photo named the same
/// first three reads, so the report could not say whether an engine never read an item or read it and the search failed.
/// </summary>
public sealed class ServerVerdictScoringTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private ScoredEngines ScoreShelf(params GroundedItem[] read)
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64"), ShelfItem("Wave Race 64")]);
        return EvalScorer.Score([Photo("s1", Apple, read)], TruthsOf(_golden));
    }

    [Fact]
    public void A_confident_read_the_server_did_not_match_is_not_an_auto_add()
    {
        var narrowed = T1(new GroundedItem("star fox", null, "model", "ambiguous", [Release("Star Fox 64")]));
        var nothing = T1(new GroundedItem("words", null, "model", "not_found", []));
        var pending = T0(new GroundedItem("more words", null, "model", "pending", []));

        var shelf = ScoreShelf(narrowed, nothing, pending).ShelfOf();

        shelf.AutoAdds.ShouldBe(0);
        shelf.AutoAddPrecisionPct.ShouldBeNull();
        shelf.T0Auto.ShouldBe(0);
        shelf.T1Auto.ShouldBe(0);
        shelf.T2Review.ShouldBe(3);
        shelf.ReviewRatePct.ShouldBe(100);
    }

    [Fact]
    public void A_confident_read_the_server_matched_is_an_auto_add()
    {
        var shelf = ScoreShelf(T1(Matched("Star Fox 64"))).ShelfOf();

        shelf.AutoAdds.ShouldBe(1);
        shelf.CorrectAutoAdds.ShouldBe(1);
        shelf.T1Auto.ShouldBe(1);
    }

    [Fact]
    public void A_missed_truth_item_names_the_closest_read_not_the_first_reads_of_the_photo()
    {
        var close = new GroundedItem("WAVE RAC 64", null, "vision_text", "ambiguous", [Release("Wave Race")]);

        var misses = ScoreShelf(Matched("Mario Kart 64"), Matched("Banjo-Kazooie"), close).Misses;

        misses.Single(m => m.Truth == "Wave Race 64").Read.ShouldBe("Wave Race [WAVE RAC 64, ambiguous]");
        misses.Single(m => m.Truth == "Star Fox 64").Read.ShouldBe("(nothing close)");
    }

    [Fact]
    public void A_read_that_found_nothing_is_named_by_what_was_read()
    {
        var misses = ScoreShelf(new GroundedItem("STAR FOX 64 NINTENDO", null, "vision_text", "not_found", [])).Misses;

        misses.Single(m => m.Truth == "Star Fox 64").Read.ShouldBe("[STAR FOX 64 NINTENDO, not_found]");
    }

    [Fact]
    public void A_read_that_is_another_truth_item_right_answer_is_not_offered_as_the_closest()
    {
        _golden.AddShelf("s1", [ShelfItem("Mario Kart 64"), ShelfItem("Mario Kart 8")]);

        var misses = EvalScorer.Score([Photo("s1", Apple, [Matched("Mario Kart 64")])], TruthsOf(_golden)).Misses;

        misses.Single(m => m.Truth == "Mario Kart 8").Read.ShouldBe("(nothing close)");
    }
}
