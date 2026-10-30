using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

/// <summary>
/// A sequel number written in Arabic or Roman figures is the same word to the title search (an OCR read says "Camp 3", the
/// catalogue says "Camp III"), while a lone "I" is a word, never a 1; and an accent an OCR read puts on an English word
/// is noise the search can drop without touching anything else of the read.
/// </summary>
public class TitleNumeralAndAccentTests
{
    [Theory]
    [InlineData("Rocky 3", "Rocky III")]
    [InlineData("Resident Evil 4", "Resident Evil IV")]
    [InlineData("Final Fantasy 12", "Final Fantasy XII")]
    [InlineData("Rocky 20", "Rocky XX")]
    [InlineData("Sleepaway Camp 3: Teenage Wasteland", "Sleepaway Camp III: Teenage Wasteland")]
    public void An_arabic_numeral_and_its_roman_numeral_are_the_same_word_in_a_similarity(string arabic, string roman)
    {
        TitleNormalizer.Similarity(arabic, roman).ShouldBe(1.0);
        TitleNormalizer.Similarity(roman, arabic).ShouldBe(1.0);
    }

    [Fact]
    public void A_different_number_is_not_the_same_word_whatever_its_figures()
    {
        TitleNormalizer.Similarity("Rocky 3", "Rocky IV").ShouldBeLessThan(1.0);
    }

    [Fact]
    public void A_lone_I_is_a_word_and_is_not_turned_into_a_1()
    {
        TitleNormalizer.Normalize("I Am Legend").ShouldNotStartWith("1");
        TitleNormalizer.Similarity("I Am Legend", "1 Am Legend").ShouldBeLessThan(1.0);
    }

    [Theory]
    [InlineData("SLEEP AWAY CAMP 3 TÉENAGE WASTELAND", "SLEEP AWAY CAMP 3 TEENAGE WASTELAND")]
    [InlineData("Pokémon Red Version", "Pokemon Red Version")]
    [InlineData("Crème Brûlée à la Française", "Creme Brulee a la Francaise")]
    [InlineData("Ångström Łódź Œuvre Straße", "Angstrom Lodz Oeuvre Strasse")]
    public void Accents_are_stripped_from_a_read_title_and_its_case_is_kept(string read, string expected)
    {
        TitleNormalizer.StripAccents(read).ShouldBe(expected);
    }

    [Fact]
    public void Stripping_accents_leaves_everything_else_of_a_title_alone()
    {
        TitleNormalizer.StripAccents("Spider-Man: Into the Spider-Verse (2018) & Co.").ShouldBe("Spider-Man: Into the Spider-Verse (2018) & Co.");
    }

    [Fact]
    public void Stripping_accents_from_nothing_gives_nothing()
    {
        TitleNormalizer.StripAccents(null).ShouldBe(string.Empty);
        TitleNormalizer.StripAccents("").ShouldBe(string.Empty);
    }
}
