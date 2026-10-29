using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Matching;

// The third eval run (2026-10-29): right books with exact titles scored about 0.8, under the Matched line, and nothing kept
// said which term pulled them down. Each scored release now lists the terms that made its score.
public class ScoreBreakdownTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Cover => Options.Profile(ScoringProfileKind.Cover);

    private static CandidateRelease Book(string key, string title, int? year, params string[] creators) =>
        new() { Key = key, Title = title, Kind = WorkKind.Book, Year = year, Creators = creators };

    [Fact]
    public void Every_term_that_took_part_is_listed_with_its_weight_and_value()
    {
        var read = new ReadItem { Title = "Harbor Light Keepers", Kind = WorkKind.Book, Year = 2020, Creator = "Mara Odell" };

        var scored = MatchScorer.Evaluate(read, [Book("k", "Harbor Light Keepers", 2020, "Mara Odell")], Cover).Ranked[0];

        scored.Terms.Select(t => t.Name).ShouldBe(["title", "type", "year", "creator"]);
        scored.Terms.ShouldAllBe(t => t.Compared && t.Value == 1.0 && t.Weight > 0);
    }

    [Fact]
    public void The_listed_terms_give_the_score_as_their_weighted_mean()
    {
        var read = new ReadItem { Title = "Harbor Light Keepers", Kind = WorkKind.Book, Year = 2020, Creator = "Someone Else" };

        var scored = MatchScorer.Evaluate(read, [Book("k", "Harbor Light Keepers", 2014, "Mara Odell")], Cover).Ranked[0];

        var mean = scored.Terms.Sum(t => t.Weight * t.Value) / scored.Terms.Sum(t => t.Weight);
        scored.Score.ShouldBe(Math.Round(mean, 9));
        scored.Terms.Single(t => t.Name == "creator").Value.ShouldBeLessThan(1.0);
        scored.Terms.Single(t => t.Name == "year").Value.ShouldBeLessThan(1.0);
    }

    [Fact]
    public void A_term_only_another_release_can_answer_is_neutral_and_not_compared()
    {
        var read = new ReadItem { Title = "Harbor Light Keepers", Kind = WorkKind.Book, Year = 2020 };

        var ranked = MatchScorer.Evaluate(read, [Book("dated", "Harbor Light Keepers", 2020), Book("undated", "Harbor Light Keepers", null)], Cover).Ranked;

        var year = ranked.Single(r => r.Release.Key == "undated").Terms.Single(t => t.Name == "year");
        year.Compared.ShouldBeFalse();
        year.Value.ShouldBe(0.5);
    }

    [Fact]
    public void A_spine_that_carries_the_author_in_the_title_lists_it_as_the_creator_term()
    {
        var read = new ReadItem { Title = "harbor light keepers ODELL" };

        var scored = MatchScorer.Evaluate(read, [Book("k", "Harbor Light Keepers", null, "Mara Odell")], Options.Profile(ScoringProfileKind.ShelfSpine)).Ranked[0];

        scored.Terms.Single(t => t.Name == "creator").ShouldBe(new ScoreTerm("creator", scored.Terms.Single(t => t.Name == "creator").Weight, 1.0, true));
    }

    [Fact]
    public void An_exact_identifier_match_lists_only_the_identifier()
    {
        var isbn = Identifier.TryParse("9781234567897", out var id) ? id! : throw new InvalidOperationException();
        var read = new ReadItem { Title = "Something else entirely", Identifiers = [isbn] };
        var release = Book("k", "Harbor Light Keepers", 2020) with { Identifiers = [isbn] };

        var scored = MatchScorer.Evaluate(read, [release], Cover).Ranked[0];

        scored.Terms.ShouldBe([new ScoreTerm("identifier", 1.0, 1.0, true)]);
    }
}
