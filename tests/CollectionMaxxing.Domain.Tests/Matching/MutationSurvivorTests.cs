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
    public void The_years_1900_and_2099_are_years_so_different_ones_are_a_known_conflict(string a, string b)
    {
        var result = MatchScorer.Evaluate(new ReadItem { Title = a }, [Release(b)], Cover);

        result.Ranked[0].HasKnownConflict.ShouldBeTrue();
    }

    [Fact]
    public void A_trailing_year_is_dropped_whatever_its_last_digit()
    {
        TitleNormalizer.Similarity("Foo 1901", "Foo").ShouldBe(1.0);
        TitleNormalizer.Similarity("Foo 2008", "Foo").ShouldBe(1.0);
    }

    [Fact]
    public void A_four_character_word_that_is_not_all_digits_is_not_a_year()
    {
        TitleNormalizer.Similarity("Alien 190a", "Alien").ShouldBeLessThan(1.0);
    }

    [Fact]
    public void Leading_punctuation_and_single_letters_keep_platform_names_tidy()
    {
        PlatformNames.Normalize("--Game  Boy!!").ShouldBe("game boy");
        PlatformNames.Normalize("a b").ShouldBe("a b");
    }

    [Fact]
    public void An_unrated_art_term_for_one_release_does_not_count_as_a_compared_signal()
    {
        var read = new ReadItem { Title = "Alpha", ArtDistances = new Dictionary<string, double> { ["r1"] = 0.1 } };
        var other = new CandidateRelease { Key = "r2", Title = "Alpha" };

        var result = MatchScorer.Evaluate(read, [Release("Alpha"), other], Cover);

        result.Ranked.Single(s => s.Release.Key == "r2").ComparedSignals.ShouldBe(1);
    }

    [Fact]
    public void Two_real_and_equal_kinds_count_as_a_compared_signal()
    {
        var read = new ReadItem { Title = "Alpha", Kind = WorkKind.Film };

        var result = MatchScorer.Evaluate(read, [Release("Alpha", WorkKind.Film)], Cover);

        result.Ranked[0].ComparedSignals.ShouldBe(2);
    }

    [Fact]
    public void A_release_with_no_creators_cannot_answer_a_creator_term()
    {
        var read = new ReadItem { Title = "Alpha", Creator = "Someone" };

        var result = MatchScorer.Evaluate(read, [Release("Alpha")], Cover);

        result.Ranked[0].ComparedSignals.ShouldBe(1);
    }

    [Fact]
    public void A_release_with_an_unknown_platform_is_neither_a_match_nor_a_mismatch_on_platform()
    {
        var read = new ReadItem { Title = "Alpha", Platform = "N64" };

        var result = MatchScorer.Evaluate(read, [Release("Alpha")], Cover);

        result.Ranked[0].HasKnownConflict.ShouldBeFalse();
        result.Ranked[0].Score.ShouldBeInRange(0.84, 0.86);
    }

    [Fact]
    public void A_release_with_no_format_is_neither_a_match_nor_a_mismatch_on_format()
    {
        var read = new ReadItem { Title = "Alpha", Format = ReleaseFormat.Dvd };

        var result = MatchScorer.Evaluate(read, [Release("Alpha")], Cover);

        result.Ranked[0].Score.ShouldBeInRange(0.88, 0.90);
    }

    [Fact]
    public void Titles_that_differ_only_in_an_annual_year_still_score_as_similar_but_not_equal()
    {
        TitleNormalizer.Similarity("Madden NFL 2005", "Madden NFL 2004").ShouldBeInRange(0.5, 0.9);
    }

    [Fact]
    public void A_year_cap_of_zero_is_a_valid_threshold()
    {
        Should.NotThrow(() => new ScoringThresholds { PlatformMismatchCap = 0, TitleYearMismatchCap = 0 }.Validate());
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
