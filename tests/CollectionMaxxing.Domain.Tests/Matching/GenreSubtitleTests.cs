using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// The book providers append a genre label to a title the cover and the catalogue carry alone, and comparing the whole of
// it with a spine read costs the match. A release is stored and offered under its title alone; a real subtitle stays.
public class GenreSubtitleTests
{
    [Theory]
    [InlineData("A Novel")]
    [InlineData("a novel")]
    [InlineData("A NOVEL")]
    [InlineData("  A  Novel  ")]
    [InlineData("A Novel.")]
    [InlineData("Novel")]
    [InlineData("A Novella")]
    [InlineData("Roman")]
    [InlineData("Ein Roman")]
    public void A_genre_label_is_dropped_from_a_providers_subtitle(string subtitle)
    {
        GenreSubtitle.Join("Dune", subtitle).ShouldBe("Dune");
    }

    [Theory]
    [InlineData("Echoes")]
    [InlineData("Adventures in the Culinary Underbelly")]
    [InlineData("Stories")]
    [InlineData("A Memoir")]
    [InlineData("A Novel Approach to Baking")]
    public void A_real_subtitle_stays(string subtitle)
    {
        GenreSubtitle.Join("Metroid Prime 2", subtitle).ShouldBe($"Metroid Prime 2: {subtitle}");
    }

    [Fact]
    public void No_subtitle_leaves_the_title_alone()
    {
        GenreSubtitle.Join("Dune", null).ShouldBe("Dune");
        GenreSubtitle.Join("Dune", "   ").ShouldBe("Dune");
    }

    [Theory]
    [InlineData("Dune: A Novel", "Dune")]
    [InlineData("Dune (A Novel)", "Dune")]
    [InlineData("Der Vorleser - Roman", "Der Vorleser")]
    [InlineData("Kitchen Confidential: Adventures: A Novel", "Kitchen Confidential: Adventures")]
    public void A_title_that_already_carries_a_genre_label_loses_it(string title, string expected)
    {
        GenreSubtitle.WithoutLabel(title).ShouldBe(expected);
    }

    [Theory]
    [InlineData("A Novel")]
    [InlineData("Novel")]
    [InlineData("Metroid Prime 2: Echoes")]
    [InlineData(": A Novel")]
    public void A_title_that_is_only_a_label_or_has_no_label_is_left_alone(string title)
    {
        GenreSubtitle.WithoutLabel(title).ShouldBe(title);
    }

    [Fact]
    public void A_read_of_the_title_alone_matches_a_release_the_provider_labelled()
    {
        TitleNormalizer.TitleSimilarity("DUNE", "Dune: A Novel").ShouldBe(1.0);
    }

    [Fact]
    public void A_release_with_a_real_subtitle_still_keeps_only_its_main_title_credit()
    {
        TitleNormalizer.TitleSimilarity("METROID PRIME 2", "Metroid Prime 2: Echoes").ShouldBe(TitleNormalizer.MainTitleCredit);
    }
}
