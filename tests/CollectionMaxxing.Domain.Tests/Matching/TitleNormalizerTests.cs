using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Locked acceptance tests for CAT-1 (docs/02 section 6; docs/09 "Domain: title normalisation and scoring").
// Do not weaken, skip or delete.
public class TitleNormalizerTests
{
    [Fact]
    public void The_Matrix_and_Matrix_The_normalise_equal()
    {
        TitleNormalizer.Normalize("The Matrix").ShouldBe(TitleNormalizer.Normalize("Matrix, The"));
    }

    [Fact]
    public void Final_Fantasy_VII_and_Final_Fantasy_7_normalise_equal()
    {
        TitleNormalizer.Normalize("Final Fantasy VII").ShouldBe(TitleNormalizer.Normalize("Final Fantasy 7"));
    }

    [Fact]
    public void Ratchet_and_Clank_with_an_ampersand_and_with_the_word_and_normalise_equal()
    {
        TitleNormalizer.Normalize("Ratchet & Clank").ShouldBe(TitleNormalizer.Normalize("Ratchet and Clank"));
    }

    [Theory]
    [InlineData("A Bug's Life", "Bugs Life")]
    [InlineData("Spider-Man 2", "SPIDER MAN 2")]
    [InlineData("Pokémon Red Version", "Pokemon Red Version")]
    [InlineData("Zelda: Ocarina of Time", "zelda   ocarina of time!")]
    [InlineData("Rocky III", "Rocky 3")]
    [InlineData("Resident Evil IV", "Resident Evil 4")]
    [InlineData("Player's Choice", "Players Choice")]
    [InlineData("Tomb Raider, The", "The Tomb Raider")]
    [InlineData("Wonder Woman, A", "A Wonder Woman")]
    public void Case_punctuation_accents_articles_and_roman_numerals_do_not_matter(string a, string b)
    {
        TitleNormalizer.Normalize(a).ShouldBe(TitleNormalizer.Normalize(b));
    }

    [Fact]
    public void A_lone_letter_that_could_be_a_numeral_is_left_alone_so_Mega_Man_X_is_not_Mega_Man_10()
    {
        TitleNormalizer.Normalize("Mega Man X").ShouldNotBe(TitleNormalizer.Normalize("Mega Man 10"));
    }

    [Theory]
    [InlineData("Alien", "Aliens")]
    [InlineData("Metroid Prime", "Metroid Prime 2: Echoes")]
    [InlineData("Final Fantasy VII", "Final Fantasy VIII")]
    public void Different_titles_do_not_normalise_equal(string a, string b)
    {
        TitleNormalizer.Normalize(a).ShouldNotBe(TitleNormalizer.Normalize(b));
    }

    [Fact]
    public void Identical_titles_are_perfectly_similar_and_unrelated_titles_are_not()
    {
        TitleNormalizer.Similarity("The Matrix", "Matrix, The").ShouldBe(1.0);
        TitleNormalizer.Similarity("The Matrix", "Casablanca").ShouldBeLessThan(0.3);
    }

    [Fact]
    public void A_small_misread_is_still_close()
    {
        TitleNormalizer.Similarity("Silent Hill", "Sllent Hlll").ShouldBeGreaterThan(0.75);
    }

    [Fact]
    public void A_trailing_year_in_a_read_title_does_not_hurt_the_match()
    {
        TitleNormalizer.Similarity("Alien 1979", "Alien").ShouldBe(1.0);
        TitleNormalizer.Similarity("Madden NFL 2005", "Madden NFL 2005").ShouldBe(1.0);
    }

    [Fact]
    public void A_sequel_number_that_is_missing_on_one_side_costs_similarity()
    {
        TitleNormalizer.Similarity("Metroid Prime", "Metroid Prime 2")
            .ShouldBeLessThan(TitleNormalizer.Similarity("Metroid Prime 2", "Metroid Prime 2"));
        TitleNormalizer.Similarity("Metroid Prime", "Metroid Prime 2").ShouldBeLessThan(0.7);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Empty_or_junk_titles_never_throw_and_never_match_anything(string? junk)
    {
        TitleNormalizer.Normalize(junk).ShouldBe(string.Empty);
        TitleNormalizer.Similarity(junk, "The Matrix").ShouldBe(0.0);
        TitleNormalizer.Similarity(junk, junk).ShouldBe(0.0);
    }

    [Fact]
    public void Similarity_is_symmetric_and_between_zero_and_one()
    {
        string[] titles = ["Alien", "Aliens", "Metroid Prime", "Metroid Prime 2: Echoes", "Final Fantasy VII", "Jaws", "Jaws 2"];
        foreach (var a in titles)
        {
            foreach (var b in titles)
            {
                var ab = TitleNormalizer.Similarity(a, b);
                ab.ShouldBe(TitleNormalizer.Similarity(b, a), 1e-12);
                ab.ShouldBeInRange(0.0, 1.0);
            }
        }
    }
}
