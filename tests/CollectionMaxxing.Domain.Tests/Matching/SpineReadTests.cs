using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// The first real eval run (2026-10-29): the Vision text engine read most spines of a shelf well enough for a person, as one
// line of title and author ("DUNE FRANK HERBERT"), and the scorer compared the whole line with the title, so the author's
// name sank the right book below the Ambiguous line.
public class SpineReadTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Spine => Options.Profile(ScoringProfileKind.ShelfSpine);

    private static CandidateRelease Book(string key, string title, params string[] creators) =>
        new() { Key = key, Title = title, Kind = WorkKind.Book, Creators = creators };

    [Fact]
    public void A_spine_that_reads_the_title_and_the_author_matches_the_authors_book()
    {
        var read = new ReadItem { Title = "DUNE FRANK HERBERT" };

        var result = MatchScorer.Evaluate(read, [Book("dune", "Dune", "Frank Herbert"), Book("messiah", "Dune Messiah", "Frank Herbert")], Spine);

        result.Ranked[0].Release.Key.ShouldBe("dune");
        result.Ranked[0].ComparedSignals.ShouldBe(2); // the author found on the spine is a compared creator
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Theory]
    [InlineData("ODELL MARA harbor light keepers")]
    [InlineData("harbor light keepers mara odell")]
    [InlineData("harbor light keepers ODELL")]
    public void The_author_can_stand_before_or_after_the_title_or_as_a_surname_alone(string spine)
    {
        var read = new ReadItem { Title = spine };

        MatchScorer.Evaluate(read, [Book("k", "Harbor Light Keepers", "Mara Odell")], Spine).Ranked[0].Score.ShouldBe(1.0);
    }

    [Fact]
    public void Another_authors_name_in_the_read_is_still_just_extra_words()
    {
        var read = new ReadItem { Title = "DUNE FRANK HERBERT" };

        MatchScorer.Score(read, Book("other", "Dune", "Someone Else"), Spine)
            .ShouldBeLessThan(MatchScorer.Score(read, Book("dune", "Dune", "Frank Herbert"), Spine));
    }

    [Fact]
    public void A_read_that_names_the_author_separately_keeps_its_own_creator_term()
    {
        var read = new ReadItem { Title = "DUNE FRANK HERBERT", Creator = "Frank Herbert" };

        var scored = MatchScorer.Evaluate(read, [Book("dune", "Dune", "Frank Herbert")], Spine).Ranked[0];

        scored.Score.ShouldBe(1.0);
        scored.ComparedSignals.ShouldBe(2);
    }

    [Fact]
    public void A_title_that_is_only_the_authors_name_is_not_emptied()
    {
        // A book titled after its author must not lose every word and score as a match on nothing.
        var read = new ReadItem { Title = "Marisol" };

        MatchScorer.Evaluate(read, [Book("n", "Marisol Express", "Marisol Varga")], Spine).Ranked[0].Score.ShouldBeLessThan(0.9);
    }
}
