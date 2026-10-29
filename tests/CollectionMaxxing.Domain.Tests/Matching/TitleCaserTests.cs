using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Golden cases for #353: provider titles arrive sentence-cased (or worse) and display headline-cased.
// Display-time only: stored titles stay verbatim, and a user's CustomTitle never passes through here.
public class TitleCaserTests
{
    [Theory]
    [InlineData("Lantern road: from harbor towns to mountain passes", "Lantern Road: From Harbor Towns to Mountain Passes")]
    [InlineData("So you want to be a wizard", "So You Want to Be a Wizard")]
    [InlineData("the lord of the rings: the fellowship of the ring", "The Lord of the Rings: The Fellowship of the Ring")]
    public void Sentence_case_provider_titles_come_out_headline_cased(string input, string expected)
    {
        TitleCaser.ToTitleCase(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("harry potter & the prisoner of azkaban", "Harry Potter & the Prisoner of Azkaban")]
    [InlineData("ratchet & clank", "Ratchet & Clank")]
    [InlineData("o'brien's army", "O'Brien's Army")]
    [InlineData("player's handbook", "Player's Handbook")]
    [InlineData("rock 'n' roll", "Rock 'n' Roll")]
    public void Ampersands_and_apostrophes_are_handled(string input, string expected)
    {
        TitleCaser.ToTitleCase(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("THE LORD OF THE RINGS", "The Lord of the Rings")]
    [InlineData("SO YOU WANT TO BE A WIZARD", "So You Want to Be a Wizard")]
    public void All_caps_provider_titles_come_out_headline_cased(string input, string expected)
    {
        TitleCaser.ToTitleCase(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("final fantasy ix", "Final Fantasy IX")]
    [InlineData("rocky ii", "Rocky II")]
    [InlineData("super bowl xxi", "Super Bowl XXI")]
    [InlineData("Mega Man X", "Mega Man X")]
    [InlineData("Brand New Mix", "Brand New Mix")]
    public void Roman_numerals_are_uppercased_but_lookalikes_are_left_alone(string input, string expected)
    {
        TitleCaser.ToTitleCase(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("The Lord of the Rings: The Fellowship of the Ring")]
    [InlineData("So You Want to Be a Wizard")]
    [InlineData("Lantern Road: From Harbor Towns to Mountain Passes")]
    [InlineData("Spider-Man: Into the Spider-Verse")]
    [InlineData("Ratchet & Clank")]
    [InlineData("2001: A Space Odyssey")]
    public void Already_correct_titles_come_out_byte_identical(string title)
    {
        TitleCaser.ToTitleCase(title).ShouldBe(title);
    }

    [Theory]
    [InlineData("Visit NASA headquarters", "Visit NASA Headquarters")]
    [InlineData("D&D player's handbook", "D&D Player's Handbook")]
    [InlineData("iPhone vs android", "iPhone vs Android")]
    [InlineData("Meet the McDonalds", "Meet the McDonalds")]
    [InlineData("ronald mcdonald", "Ronald McDonald")]
    [InlineData("spider-man: far from home", "Spider-Man: Far from Home")]
    [InlineData("x-men: days of future past", "X-Men: Days of Future Past")]
    [InlineData("catch-22", "Catch-22")]
    [InlineData("2001: a space odyssey", "2001: A Space Odyssey")]
    [InlineData("madden NFL 2005", "Madden NFL 2005")]
    public void Acronyms_internal_capitals_names_hyphens_and_numbers_survive(string input, string expected)
    {
        TitleCaser.ToTitleCase(input).ShouldBe(expected);
    }

    // Chosen behaviour for #353: an all-lowercase stylised title is cased like any other provider title,
    // because stored provider data is never intentionally stylised. A person who wants the lowercase
    // look sets it as their CustomTitle, which is never re-cased.
    [Theory]
    [InlineData("in rainbows", "In Rainbows")]
    [InlineData("bell hooks: a memoir", "Bell Hooks: A Memoir")]
    public void Intentionally_stylised_lowercase_titles_are_cased_like_any_other(string input, string expected)
    {
        TitleCaser.ToTitleCase(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("a", "A")]
    [InlineData("the", "The")]
    public void Empty_and_tiny_titles_never_throw(string? input, string expected)
    {
        TitleCaser.ToTitleCase(input).ShouldBe(expected);
    }
}
