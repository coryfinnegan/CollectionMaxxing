using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Cover reads often carry only the main title while providers answer "Title: Subtitle" (Google Books, Open Library
// editions), or the other way round (Open Library work titles drop the subtitle the cover prints).
public class SubtitleAndPlatformTests
{
    [Theory]
    [InlineData("Kitchen Confidential", "Kitchen Confidential: Adventures in the Culinary Underbelly")]
    [InlineData("Sapiens: A Brief History of Humankind", "Sapiens")]
    [InlineData("Dune", "Dune (Dune Chronicles, Book 1)")]
    [InlineData("Halo", "Halo - Combat Evolved")]
    public void A_title_that_is_the_other_ones_main_title_is_close(string read, string release)
    {
        TitleNormalizer.Similarity(read, release).ShouldBeGreaterThanOrEqualTo(0.8);
        TitleNormalizer.Similarity(release, read).ShouldBe(TitleNormalizer.Similarity(read, release), 1e-12);
    }

    [Fact]
    public void A_missing_subtitle_never_scores_as_the_same_title()
    {
        TitleNormalizer.Similarity("Kitchen Confidential", "Kitchen Confidential: Adventures in the Culinary Underbelly")
            .ShouldBeLessThan(TitleNormalizer.Similarity("Kitchen Confidential", "Kitchen Confidential"));
    }

    [Theory]
    [InlineData("The Legend of Zelda: Ocarina of Time", "The Legend of Zelda: Majora's Mask")]
    [InlineData("Star Wars: A New Hope", "Star Wars: The Empire Strikes Back")]
    public void Two_different_subtitles_of_one_series_earn_no_main_title_credit(string a, string b)
    {
        TitleNormalizer.Similarity(a, b).ShouldBeLessThan(TitleNormalizer.MainTitleCredit);
    }

    [Fact]
    public void A_subtitle_does_not_hide_a_sequel_number()
    {
        TitleNormalizer.Similarity("Metroid Prime", "Metroid Prime 2: Echoes").ShouldBeLessThan(0.7);
    }

    [Theory]
    [InlineData("Sega Mega Drive/Genesis", "Genesis")]
    [InlineData("PC (Microsoft Windows)", "PC")]
    [InlineData("TurboGrafx-16/PC Engine", "TurboGrafx-16")]
    [InlineData("Sega Master System/Mark III", "Master System")]
    [InlineData("Family Computer", "NES")]
    [InlineData("Nintendo Switch Lite", "Switch")]
    [InlineData("Atari 2600", "Atari 2600")]
    [InlineData("Sega CD", "Sega CD")]
    public void Provider_spellings_of_more_consoles_are_known(string name, string canonical)
    {
        PlatformNames.TryGetCanonical(name, out var found).ShouldBeTrue();
        found.ShouldBe(canonical);
    }

    [Theory]
    [InlineData("boxed")]
    [InlineData("book")]
    [InlineData("manualVisible")]
    public void Words_that_name_no_console_are_not_known(string word)
    {
        PlatformNames.IsKnown(word).ShouldBeFalse();
    }
}
