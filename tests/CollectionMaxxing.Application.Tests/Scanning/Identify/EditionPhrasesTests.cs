using CollectionMaxxing.Application.Scanning.Identify;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A case prints its edition after the title ("HIGHLANDER: DIRECTOR'S CUT"), and the reader gives the lot as the title (#815). This is
// which trailing words are an edition phrase, and what is left of the title without them. Film titles are public ones or neutral.
public class EditionPhrasesTests
{
    [Theory]
    [InlineData("HIGHLANDER: DIRECTOR'S CUT", "HIGHLANDER", "DIRECTOR'S CUT")] // the read that started it
    [InlineData("HIGHLANDER DIRECTOR'S CUT", "HIGHLANDER", "DIRECTOR'S CUT")] // the same read without the colon
    [InlineData("Highlander (Director's Cut)", "Highlander", "Director's Cut")]
    [InlineData("Highlander - Director’s Cut", "Highlander", "Director’s Cut")] // a curly apostrophe and a spaced dash
    [InlineData("Copper Lantern Special Edition", "Copper Lantern", "Special Edition")]
    [InlineData("Copper Lantern: 25th Anniversary Edition", "Copper Lantern", "25th Anniversary Edition")] // the ordinal goes with it
    [InlineData("COPPER LANTERN WIDESCREEN", "COPPER LANTERN", "WIDESCREEN")]
    [InlineData("Copper Lantern Special Edition Widescreen", "Copper Lantern", "Special Edition Widescreen")] // two phrases
    [InlineData("Copper Lantern 2 Special Edition", "Copper Lantern 2", "Special Edition")] // a bare number is the title's own
    [InlineData("Up Widescreen", "Up", "Widescreen")] // a real two-letter title word is enough to be left
    [InlineData("Highlander - Director's Cut [Widescreen]", "Highlander", "Director's Cut Widescreen")] // brackets go on both sides
    public void A_trailing_edition_phrase_is_split_off_the_title(string read, string title, string phrase)
    {
        var split = EditionPhrases.Trailing(read);

        split.ShouldNotBeNull();
        split.Title.ShouldBe(title);
        split.Phrase.ShouldBe(phrase);
    }

    [Theory]
    [InlineData("The Final Cut")] // a real title that ends in "Cut"
    [InlineData("Director's Cut")] // a title that is exactly an edition phrase keeps its words
    [InlineData("DIRECTOR'S CUT")]
    [InlineData("25th Anniversary Edition")]
    [InlineData("The Director's Cut")] // an article alone is no title
    [InlineData("A Special Edition")]
    [InlineData("The Collector's Edition")]
    [InlineData("2 Special Edition")] // nor is a number alone
    [InlineData("- Director's Cut")] // nor punctuation
    [InlineData("Special Edition: Copper Lantern")] // only a phrase at the end counts
    [InlineData("Highlander")]
    [InlineData("")]
    [InlineData(null)]
    public void A_title_without_a_trailing_edition_phrase_or_with_nothing_else_is_left_alone(string? read)
    {
        EditionPhrases.Trailing(read).ShouldBeNull();
    }

    [Theory]
    [InlineData("DIRECTOR'S CUT", true)]
    [InlineData("director's cut", true)]
    [InlineData("25th Anniversary Edition", true)]
    [InlineData("Widescreen", true)]
    [InlineData("Highlander Director's Cut", false)]
    [InlineData("Cut", false)]
    [InlineData("", false)]
    public void Only_an_edition_phrase_and_nothing_else_is_an_edition_phrase_alone(string text, bool alone)
    {
        EditionPhrases.IsOnlyAPhrase(text).ShouldBe(alone);
    }
}
