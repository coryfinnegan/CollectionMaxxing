using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The second shelf run (#509). The phone tiers every confident read T1, so reads the server only narrowed down, or found
/// nothing for, scored as wrong auto-adds that the server would never have made. And every miss of a photo named the same
/// first three reads, so the report could not say whether an engine never read a book or read it and the search failed.
/// </summary>
public sealed class ServerVerdictScoringTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private static GroundedCandidate Book(string title) => Release(title, platform: null, kind: "book", format: "hardcover");

    private static GroundedItem MatchedBook(string title) => new(title, null, "model", "matched", [Book(title)]);

    private ScoredEngines ScoreBookShelf(params GroundedItem[] read)
    {
        _golden.AddShelf("s1", [ShelfItem("The Staub Cookbook", "book", null), ShelfItem("Good Eats: The Early Years", "book", null)]);
        return EvalScorer.Score([Photo("s1", Apple, read)], TruthsOf(_golden));
    }

    [Fact]
    public void A_confident_read_the_server_did_not_match_is_not_an_auto_add()
    {
        var narrowed = T1(new GroundedItem("staub", null, "model", "ambiguous", [Book("The Staub Cookbook")]));
        var nothing = T1(new GroundedItem("words", null, "model", "not_found", []));

        var shelf = ScoreBookShelf(narrowed, nothing).ShelfOf();

        shelf.AutoAdds.ShouldBe(0);
        shelf.AutoAddPrecisionPct.ShouldBeNull();
        shelf.T1Auto.ShouldBe(0);
        shelf.T2Review.ShouldBe(2);
        shelf.ReviewRatePct.ShouldBe(100);
    }

    [Fact]
    public void A_confident_read_the_server_matched_is_an_auto_add()
    {
        var shelf = ScoreBookShelf(T1(MatchedBook("The Staub Cookbook"))).ShelfOf();

        shelf.AutoAdds.ShouldBe(1);
        shelf.CorrectAutoAdds.ShouldBe(1);
        shelf.T1Auto.ShouldBe(1);
    }

    [Fact]
    public void A_missed_truth_item_names_the_closest_read_not_the_first_reads_of_the_photo()
    {
        var close = new GroundedItem("GOOD FEAIS", null, "vision_text", "ambiguous", [Book("Good Eats")]);

        var misses = ScoreBookShelf(MatchedBook("Simple Beautiful Food"), MatchedBook("Salt, Fat, Acid, Heat"), close).Misses;

        misses.Single(m => m.Truth == "Good Eats: The Early Years").Read.ShouldBe("Good Eats [GOOD FEAIS, ambiguous]");
        misses.Single(m => m.Truth == "The Staub Cookbook").Read.ShouldBe("(nothing close)");
    }

    [Fact]
    public void A_read_that_found_nothing_is_named_by_what_was_read()
    {
        var misses = ScoreBookShelf(new GroundedItem("THE Staub COOKBOOK FRY", null, "vision_text", "not_found", [])).Misses;

        misses.Single(m => m.Truth == "The Staub Cookbook").Read.ShouldBe("[THE Staub COOKBOOK FRY, not_found]");
    }

    [Fact]
    public void A_read_that_is_another_truth_item_right_answer_is_not_offered_as_the_closest()
    {
        _golden.AddShelf("s1", [ShelfItem("Barefoot Contessa at Home", "book", null), ShelfItem("Barefoot Contessa Family Style", "book", null)]);

        var misses = EvalScorer.Score([Photo("s1", Apple, [MatchedBook("Barefoot Contessa at Home")])], TruthsOf(_golden)).Misses;

        misses.Single(m => m.Truth == "Barefoot Contessa Family Style").Read.ShouldBe("(nothing close)");
    }
}
