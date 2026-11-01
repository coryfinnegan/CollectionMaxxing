using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// A text reader given an English spine sometimes answers a letter of another alphabet in place of the Latin one it looks
// like. The two keys then share no trigram at all, so the catalogue search misses and the title scores badly.
public class LookAlikeLetterTests
{
    [Theory]
    [InlineData("ОDELL harbor light", "odell harbor light")]   // Cyrillic O
    [InlineData("HARВOR LIGHT", "harbor light")]               // Cyrillic VE for B
    [InlineData("ΗARBOR LIGHT", "harbor light")]               // Greek Eta for H
    [InlineData("MARА ОDELL", "mara odell")]              // Cyrillic A and O
    [InlineData("РОKEMON RED", "pokemon red")]        // Cyrillic ER and O
    public void A_look_alike_letter_is_read_as_the_latin_letter_it_resembles(string read, string key)
    {
        TitleNormalizer.Normalize(read).ShouldBe(key);
    }

    [Fact]
    public void A_read_with_look_alike_letters_still_matches_its_release()
    {
        TitleNormalizer.TitleSimilarity("HARВОR LIGHT KEEPERS", "Harbor Light Keepers").ShouldBe(1.0);
    }

    [Fact]
    public void A_search_query_loses_its_look_alike_letters_and_keeps_its_case()
    {
        TitleNormalizer.StripAccents("HARВOR Light").ShouldBe("HARBOR Light");
    }

    [Fact]
    public void A_title_written_wholly_in_another_alphabet_keeps_its_own_letters()
    {
        // No Latin letter is being read here, so these are Russian words rather than a misread of English ones.
        TitleNormalizer.Normalize("Привет мир").ShouldBe("привет мир");
        TitleNormalizer.StripAccents("Привет").ShouldBe("Привет");
    }

    [Fact]
    public void A_title_in_another_alphabet_still_matches_itself()
    {
        var title = "Привет мир";

        TitleNormalizer.TitleSimilarity(title, title).ShouldBe(1.0);
    }

    [Fact]
    public void A_letter_that_looks_like_nothing_latin_is_left_alone()
    {
        // Cyrillic PE resembles no Latin letter, so it stays as it was read.
        TitleNormalizer.Normalize("X П B").ShouldBe("x п b");
    }
}
