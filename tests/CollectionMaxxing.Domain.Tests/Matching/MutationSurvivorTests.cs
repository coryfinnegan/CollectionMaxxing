using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Added by QA-3b: each test pins a behaviour a mutation sweep showed nothing was checking.
public class MutationSurvivorTests
{
    private static readonly ScoringProfile Cover = new ScoringOptions().Profile(ScoringProfileKind.Cover);

    private static CandidateRelease Release(string title, WorkKind kind = WorkKind.Other) =>
        new() { Key = "r1", Title = title, Kind = kind };

    [Fact]
    public void A_cover_art_comparison_counts_as_a_compared_signal()
    {
        var read = new ReadItem { Title = "Alpha", ArtDistances = new Dictionary<string, double> { ["r1"] = 0.1 } };

        var result = MatchScorer.Evaluate(read, [Release("Alpha")], Cover);

        result.Ranked[0].ComparedSignals.ShouldBe(2);
    }

    [Fact]
    public void A_kind_of_Other_on_the_read_side_is_not_compared_against_a_real_kind()
    {
        var read = new ReadItem { Title = "Alpha", Kind = WorkKind.Other };

        var result = MatchScorer.Evaluate(read, [Release("Alpha", WorkKind.Film)], Cover);

        result.Ranked[0].ComparedSignals.ShouldBe(1);
    }

    [Fact]
    public void A_two_word_title_keeps_its_words_after_the_leading_article_is_dropped()
    {
        TitleNormalizer.Normalize("The Matrix").ShouldBe("matrix");
        TitleNormalizer.Normalize("The").ShouldBe("the");
    }

    [Fact]
    public void A_one_character_title_matches_itself()
    {
        TitleNormalizer.Similarity("M", "M").ShouldBe(1.0);
    }

    [Theory]
    [InlineData("Alien", "Alien 1979")]
    [InlineData("Alien 1979", "Alien")]
    public void A_year_on_only_one_side_is_ignored_in_either_direction(string a, string b)
    {
        TitleNormalizer.Similarity(a, b).ShouldBe(1.0);
    }

    [Fact]
    public void Word_order_does_not_matter()
    {
        TitleNormalizer.Similarity("Zelda Link", "Link Zelda").ShouldBe(1.0);
    }

    [Theory]
    [InlineData("Foo 1900", "Foo 1901")]
    [InlineData("Foo 2099", "Foo 2098")]
    public void The_years_1900_and_2099_are_years_so_different_ones_conflict(string a, string b)
    {
        // A conflict keeps the year as a word (about 0.66); a mistaken "no year" would strip it and score about 0.4.
        TitleNormalizer.Similarity(a, b).ShouldBeGreaterThan(0.6);
    }

    [Fact]
    public void Known_platform_phrases_are_recognised_and_unknown_ones_are_not()
    {
        PlatformNames.TryGetCanonical("Nintendo 64", out var known).ShouldBeTrue();
        known.ShouldBe("N64");

        PlatformNames.TryGetCanonical("Banana Console", out var unknown).ShouldBeFalse();
        unknown.ShouldBe(string.Empty);
    }
}
