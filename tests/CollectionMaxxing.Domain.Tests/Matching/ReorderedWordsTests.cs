using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// The twelfth eval run (2026-10-30): the model read a shelf spine's words in the wrong order, and a catalogue title with
// those words came back Matched, an automatic add. The two titles share every word, and the word-overlap ratio forgives
// word order, so the title term was a perfect 1.0 and the near-title guard (which looks for a title below 1.0) never
// fired. The same words in another order are a different title: only author names keep forgiving order ("Odell, Mara").
public class ReorderedWordsTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Spine => Options.Profile(ScoringProfileKind.ShelfSpine);

    private static CandidateRelease Book(string title, string key = "k", int? year = null, params string[] creators) =>
        new() { Key = key, Title = title, Kind = WorkKind.Book, Year = year, Creators = creators };

    // ---- the scorer ----------------------------------------------------------------------------

    [Fact]
    public void A_title_with_the_same_words_in_another_order_is_not_Matched_on_title_and_type_alone()
    {
        var read = new ReadItem { Title = "kitchen orchard", Kind = WorkKind.Book };

        var result = MatchScorer.Evaluate(read, [Book("The Orchard Kitchen")], Spine);

        result.Ranked[0].Terms.Single(t => t.Name == "title").Value.ShouldBeLessThan(1.0);
        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void A_reordered_title_stays_a_candidate_so_the_person_can_pick_it()
    {
        var read = new ReadItem { Title = "kitchen orchard", Kind = WorkKind.Book };

        var result = MatchScorer.Evaluate(read, [Book("The Orchard Kitchen")], Spine);

        result.Ranked[0].Score.ShouldBeGreaterThanOrEqualTo(Options.Thresholds.AmbiguousScore);
    }

    [Fact]
    public void The_same_title_in_the_same_order_still_matches_without_its_article()
    {
        var read = new ReadItem { Title = "ORCHARD KITCHEN", Kind = WorkKind.Book };

        var result = MatchScorer.Evaluate(read, [Book("The Orchard Kitchen")], Spine);

        result.Ranked[0].Terms.Single(t => t.Name == "title").Value.ShouldBe(1.0);
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_title_with_its_article_moved_to_the_end_still_matches()
    {
        var read = new ReadItem { Title = "Orchard Kitchen, The", Kind = WorkKind.Book };

        MatchScorer.Evaluate(read, [Book("The Orchard Kitchen")], Spine).Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_title_in_the_right_order_outranks_the_same_words_reordered_whatever_the_input_order()
    {
        var read = new ReadItem { Title = "orchard kitchen", Kind = WorkKind.Book };
        var reordered = Book("The Kitchen Orchard", "reordered");
        var right = Book("Orchard Kitchen", "right");

        MatchScorer.Evaluate(read, [reordered, right], Spine).Ranked[0].Release.Key.ShouldBe("right");
        MatchScorer.Evaluate(read, [right, reordered], Spine).Ranked[0].Release.Key.ShouldBe("right");
    }

    [Fact]
    public void A_reordered_title_with_the_author_also_read_can_still_match()
    {
        var read = new ReadItem { Title = "kitchen orchard", Kind = WorkKind.Book, Creator = "Mara Odell" };

        var result = MatchScorer.Evaluate(read, [Book("The Orchard Kitchen", "k", null, "Mara Odell")], Spine);

        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void An_author_written_surname_first_is_still_the_same_author()
    {
        var read = new ReadItem { Title = "Orchard Kitchen", Kind = WorkKind.Book, Creator = "Odell, Mara" };

        var result = MatchScorer.Evaluate(read, [Book("The Orchard Kitchen", "k", null, "Mara Odell")], Spine);

        result.Ranked[0].Terms.Single(t => t.Name == "creator").Value.ShouldBe(1.0);
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    // ---- the title comparison ------------------------------------------------------------------

    [Fact]
    public void Reordered_words_are_close_but_never_the_same_title()
    {
        var score = TitleNormalizer.TitleSimilarity("Kitchen Orchard", "Orchard Kitchen");

        score.ShouldBeGreaterThanOrEqualTo(TitleNormalizer.ReorderedWordsCredit);
        score.ShouldBeLessThan(1.0);
    }

    [Theory]
    [InlineData("The Orchard Kitchen", "Orchard Kitchen")]
    [InlineData("Orchard Kitchen, The", "The Orchard Kitchen")]
    [InlineData("A Lantern Road", "Lantern Road, A")]
    [InlineData("Harbor Light 3", "Harbor Light III")]
    [InlineData("Harbor Light 2", "Harbor Light II")]
    [InlineData("Salt & Pepper", "Salt and Pepper")]
    [InlineData("Orchard Kitchen 1979", "Orchard Kitchen")]
    [InlineData("Café Orchard", "Cafe Orchard")]
    public void The_same_title_written_differently_is_still_exact(string a, string b)
    {
        TitleNormalizer.TitleSimilarity(a, b).ShouldBe(1.0);
        TitleNormalizer.TitleSimilarity(b, a).ShouldBe(1.0);
    }

    [Fact]
    public void A_missing_subtitle_keeps_its_main_title_credit()
    {
        TitleNormalizer.TitleSimilarity("Orchard Kitchen", "Orchard Kitchen: Recipes for a Small Plot")
            .ShouldBe(TitleNormalizer.MainTitleCredit);
    }

    [Fact]
    public void A_reordered_main_title_earns_less_than_the_main_title_credit()
    {
        TitleNormalizer.TitleSimilarity("Kitchen Orchard", "Orchard Kitchen: Recipes for a Small Plot")
            .ShouldBeLessThan(TitleNormalizer.MainTitleCredit);
    }

    [Fact]
    public void Different_years_in_the_titles_still_keep_them_apart()
    {
        TitleNormalizer.TitleSimilarity("Harbor Cup 2005", "Harbor Cup 2004").ShouldBeLessThan(1.0);
    }

    [Fact]
    public void A_sequel_number_only_one_side_has_still_costs_a_quarter()
    {
        TitleNormalizer.TitleSimilarity("Harbor Light", "Harbor Light 2").ShouldBeLessThan(0.7);
    }

    [Fact]
    public void Similarity_for_names_still_forgives_word_order()
    {
        TitleNormalizer.Similarity("Odell Mara", "Mara Odell").ShouldBe(1.0);
    }
}
