using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Matching;

// The thirteenth eval run (2026-10-30): the model read "2000s", a fragment of a "2000 Walt Disney World" tape, and it was
// Matched, an automatic add, to a book titled "2000s". A title that is almost all digits has too little in it to tell one
// thing from another, so the exact title is no proof the read is that item. It stays a candidate for a person to pick.
public class AllDigitTitleTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Spine => Options.Profile(ScoringProfileKind.ShelfSpine);

    private static CandidateRelease Book(string title, int? year = null, params string[] creators) =>
        new() { Key = "k", Title = title, Kind = WorkKind.Book, Year = year, Creators = creators };

    [Fact]
    public void A_fragment_with_one_letter_is_not_Matched_to_a_book_with_the_same_title()
    {
        var read = new ReadItem { Title = "2000s", Kind = WorkKind.Book };

        var result = MatchScorer.Evaluate(read, [Book("2000s")], Spine);

        result.Ranked[0].Terms.Single(t => t.Name == "title").Value.ShouldBe(1.0, "the title is the same; the read is just too thin to trust");
        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void A_fragment_with_one_letter_is_still_a_candidate_so_the_person_can_pick_it()
    {
        var read = new ReadItem { Title = "2000s", Kind = WorkKind.Book };

        var result = MatchScorer.Evaluate(read, [Book("2000s")], Spine);

        result.Ranked.Count.ShouldBe(1);
        result.Ranked[0].Release.Title.ShouldBe("2000s");
        result.Ranked[0].Score.ShouldBeGreaterThanOrEqualTo(Options.Thresholds.MatchedScore);
    }

    [Theory]
    [InlineData("1984")]
    [InlineData("1999")]
    [InlineData("24")]
    [InlineData("2000s")]
    [InlineData("101")]
    [InlineData("A1")]
    [InlineData("Up")]
    public void A_title_with_fewer_than_three_letters_is_not_Matched(string title)
    {
        var read = new ReadItem { Title = title, Kind = WorkKind.Book };

        MatchScorer.Evaluate(read, [Book(title)], Spine).Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void Digits_punctuation_and_spaces_do_not_count_as_letters()
    {
        var read = new ReadItem { Title = "19 84 - 20 00s", Kind = WorkKind.Book };

        MatchScorer.Evaluate(read, [Book("19 84 - 20 00s")], Spine).Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void A_title_with_fewer_than_three_letters_is_not_Matched_whatever_else_agrees()
    {
        var read = new ReadItem { Title = "1984", Kind = WorkKind.Book, Creator = "George Orwell", Year = 1949 };

        var result = MatchScorer.Evaluate(read, [Book("1984", 1949, "George Orwell")], Spine);

        result.Ranked[0].Score.ShouldBeGreaterThanOrEqualTo(Options.Thresholds.MatchedScore);
        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void An_exact_identifier_match_still_matches_a_title_with_no_letters()
    {
        var id = new Identifier(IdentifierType.Isbn13, "9780306406157");
        var read = new ReadItem { Title = "1984", Kind = WorkKind.Book, Identifiers = [id] };
        var release = new CandidateRelease { Key = "k", Title = "Nineteen Eighty-Four", Kind = WorkKind.Book, Identifiers = [id] };

        MatchScorer.Evaluate(read, [release], Spine).Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Theory]
    [InlineData("Dune")]
    [InlineData("Alien")]
    [InlineData("Wii Sports")]
    [InlineData("Halo 3")]
    [InlineData("Madden NFL 2005")]
    [InlineData("Ark")]
    [InlineData("2000 Walt Disney World")]
    public void A_title_with_three_letters_or_more_is_unaffected(string title)
    {
        var read = new ReadItem { Title = title, Kind = WorkKind.Book };

        MatchScorer.Evaluate(read, [Book(title)], Spine).Outcome.ShouldBe(MatchOutcome.Matched);
    }
}
