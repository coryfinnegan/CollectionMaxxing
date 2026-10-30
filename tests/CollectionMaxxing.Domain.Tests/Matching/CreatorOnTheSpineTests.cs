using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Eval run of 2026-10-30: the Vision engine guessed an author for each spine from its smaller lines and often sent a
// fragment or other print. The author printed in the title line was found, but only when the read carried no author at
// all, so a junk author hid the real one and the right book dropped under the Matched line.
public class CreatorOnTheSpineTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Spine => Options.Profile(ScoringProfileKind.ShelfSpine);

    private static CandidateRelease Book(string key, string title, params string[] creators) =>
        new() { Key = key, Title = title, Kind = WorkKind.Book, Creators = creators };

    private static ScoreTerm CreatorTerm(ScoredRelease scored) => scored.Terms.Single(t => t.Name == "creator");

    [Fact]
    public void A_junk_author_read_does_not_hide_the_author_found_in_the_title()
    {
        var read = new ReadItem { Title = "Harbor Light Keepers MARA ODELL", Creator = "FRE" };

        var scored = MatchScorer.Evaluate(read, [Book("k", "Harbor Light Keepers", "Mara Odell")], Spine).Ranked[0];

        CreatorTerm(scored).Value.ShouldBe(1.0);
        CreatorTerm(scored).Compared.ShouldBeTrue();
        scored.Score.ShouldBe(1.0);
    }

    [Fact]
    public void A_read_author_that_names_the_real_author_still_counts_as_one_compared_creator()
    {
        var read = new ReadItem { Title = "Harbor Light Keepers MARA ODELL", Creator = "Mara Odell" };

        var scored = MatchScorer.Evaluate(read, [Book("k", "Harbor Light Keepers", "Mara Odell")], Spine).Ranked[0];

        scored.Terms.Count(t => t.Name == "creator").ShouldBe(1);
        scored.ComparedSignals.ShouldBe(2);
        scored.Score.ShouldBe(1.0);
    }

    [Fact]
    public void A_junk_author_read_still_costs_a_release_whose_author_is_not_in_the_title()
    {
        var read = new ReadItem { Title = "Harbor Light Keepers", Creator = "FRE" };

        var scored = MatchScorer.Evaluate(read, [Book("k", "Harbor Light Keepers", "Mara Odell")], Spine).Ranked[0];

        CreatorTerm(scored).Value.ShouldBeLessThan(0.5);
        scored.Score.ShouldBeLessThan(1.0);
    }

    [Fact]
    public void The_authors_name_in_the_title_wins_over_a_wrong_author_read_for_the_book_that_has_that_author()
    {
        var read = new ReadItem { Title = "DUNE FRANK HERBERT", Creator = "Someone Else" };
        var dune = Book("dune", "Dune", "Frank Herbert");

        var scored = MatchScorer.Evaluate(read, [dune], Spine).Ranked[0];

        CreatorTerm(scored).Value.ShouldBe(1.0);
        scored.Score.ShouldBe(1.0);
    }

    [Fact]
    public void A_read_author_is_still_compared_with_the_authors_of_a_release_whose_own_author_is_not_in_the_title()
    {
        var read = new ReadItem { Title = "DUNE FRANK HERBERT", Creator = "Someone Else" };

        var scored = MatchScorer.Evaluate(read, [Book("other", "Dune", "Someone Else")], Spine).Ranked[0];

        CreatorTerm(scored).Value.ShouldBe(1.0); // the read author is this release's author: the same comparison as ever
        scored.Score.ShouldBeLessThan(1.0); // and the extra words of the title still cost it
    }

    [Fact]
    public void A_read_with_no_author_finds_the_one_in_the_title_as_before()
    {
        var read = new ReadItem { Title = "Harbor Light Keepers MARA ODELL" };

        var scored = MatchScorer.Evaluate(read, [Book("k", "Harbor Light Keepers", "Mara Odell")], Spine).Ranked[0];

        CreatorTerm(scored).Value.ShouldBe(1.0);
        scored.Score.ShouldBe(1.0);
    }
}
