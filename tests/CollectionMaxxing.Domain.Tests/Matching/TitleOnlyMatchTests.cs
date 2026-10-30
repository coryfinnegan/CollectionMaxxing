using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// The sixth eval run (2026-10-29): a made-up spine read matched a 1932 pamphlet at 0.94 on a near title and "it is a
// book", and the book providers answer only books, so the kind agreed with everything. With nothing else compared, only
// an exact title may match.
public class TitleOnlyMatchTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Spine => Options.Profile(ScoringProfileKind.ShelfSpine);

    private static CandidateRelease Book(string title, int? year = null, params string[] creators) =>
        new() { Key = "k", Title = title, Kind = WorkKind.Book, Year = year, Creators = creators };

    [Fact]
    public void A_near_title_with_only_the_kind_compared_is_Ambiguous()
    {
        var read = new ReadItem { Title = "lantern road dinners", Kind = WorkKind.Book };

        var result = MatchScorer.Evaluate(read, [Book("A Lantern Road Dinner")], Spine);

        result.Ranked[0].Score.ShouldBeGreaterThanOrEqualTo(0.90);
        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void An_exact_title_with_only_the_kind_compared_still_matches()
    {
        var read = new ReadItem { Title = "THE LANTERN ROAD COOKBOOK", Kind = WorkKind.Book };

        MatchScorer.Evaluate(read, [Book("The Lantern Road Cookbook")], Spine).Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_near_title_with_the_author_compared_still_matches()
    {
        var read = new ReadItem { Title = "lantern road dinners", Kind = WorkKind.Book, Creator = "Mara Odell" };

        MatchScorer.Evaluate(read, [Book("A Lantern Road Dinner", null, "Mara Odell")], Spine).Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_near_title_whose_spine_carries_the_author_still_matches()
    {
        var read = new ReadItem { Title = "lantern road dinners ODELL", Kind = WorkKind.Book };

        MatchScorer.Evaluate(read, [Book("A Lantern Road Dinner", null, "Mara Odell")], Spine).Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_near_title_with_the_year_compared_still_matches()
    {
        var read = new ReadItem { Title = "lantern road dinners", Kind = WorkKind.Book, Year = 2010 };

        MatchScorer.Evaluate(read, [Book("A Lantern Road Dinner", 2010)], Spine).Outcome.ShouldBe(MatchOutcome.Matched);
    }
}
