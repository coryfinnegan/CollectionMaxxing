using CollectionMaxxing.Application.Scanning.Identify;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A read that finds nothing is asked again without its sequel number: the number is the commonest thing a cover or spine gets
// wrong, and a number the catalogue does not hold finds nothing at all. This is which words count as a sequel number, and what is
// left of the title without it.
public class SequelNumberTests
{
    [Theory]
    [InlineData("WING COMMANDER II THE PRICE OF FREEDOM", "WING COMMANDER THE PRICE OF FREEDOM", "II")]
    [InlineData("Final Fantasy VII Remake", "Final Fantasy Remake", "VII")]
    [InlineData("FREE WILLY 3 THE RESCUE", "FREE WILLY THE RESCUE", "3")]
    [InlineData("Mega Man X Legacy", "Mega Man Legacy", "X")]
    [InlineData("Quiet Harbor XX Nights", "Quiet Harbor Nights", "XX")]
    [InlineData("Quiet Harbor 20 Nights", "Quiet Harbor Nights", "20")]
    [InlineData("Quiet Harbor iv Nights", "Quiet Harbor Nights", "iv")]
    [InlineData("QUIET HARBOR IV: THE LONG WAY", "QUIET HARBOR THE LONG WAY", "IV")] // the colon goes with the number
    [InlineData("Quiet Harbor (2) Nights", "Quiet Harbor Nights", "2")]
    [InlineData("Quiet Harbor 2, The Long Way", "Quiet Harbor The Long Way", "2")]
    [InlineData("  Quiet   Harbor   3  Nights ", "Quiet Harbor Nights", "3")] // spacing is only spacing
    [InlineData("3 MEN AND A BABY", "MEN AND A BABY", "3")]
    [InlineData("QUIET 3 HARBOR", "QUIET HARBOR", "3")] // two words left are enough
    public void A_whole_word_that_is_a_roman_numeral_from_two_to_twenty_or_a_number_from_two_to_twenty_is_dropped(
        string title, string left, string token)
    {
        var drop = SequelNumber.For(title);

        drop.ShouldNotBeNull();
        drop.Title.ShouldBe(left);
        drop.Token.ShouldBe(token);
    }

    [Theory]
    [InlineData("I, ROBOT")] // "I" is never a sequel number (and two words)
    [InlineData("WHERE I LEFT OFF")] // nor is it one in the middle of a title
    [InlineData("1984")] // a year is no sequel
    [InlineData("BLADE RUNNER 2049")]
    [InlineData("QUIET HARBOR 21 NIGHTS")] // past twenty
    [InlineData("QUIET HARBOR XXI NIGHTS")]
    [InlineData("QUIET HARBOR 1 NIGHTS")] // a one is no sequel
    [InlineData("QUIET HARBOR 02 NIGHTS")] // a number with a leading zero is no sequel number
    [InlineData("CATCH-22 AT SEA")] // glued to letters
    [InlineData("QUIET HARBOR 3D NIGHTS")]
    [InlineData("QUIET HARBOR 2ND NIGHTS")]
    [InlineData("QUIET HARBOR II2 NIGHTS")]
    [InlineData("QUIET HARBOR MIX NIGHTS")] // letters that are no numeral
    [InlineData("HARRY POTTER 7 8")] // two of them: which is the sequel's?
    [InlineData("2 FAST 2 FURIOUS")]
    [InlineData("QUIET HARBOR II III")]
    [InlineData("QUIET HARBOR NIGHTS")] // none
    [InlineData("ROCKY II")] // one word left
    public void Anything_else_is_no_sequel_number_so_nothing_is_dropped(string title)
    {
        SequelNumber.For(title).ShouldBeNull();
    }

    [Theory]
    [InlineData("ROCKY II")]
    [InlineData("MALCOLM X")]
    [InlineData("QUIET 3")]
    [InlineData("4 HARBOR")]
    public void A_title_that_is_left_with_fewer_than_two_words_is_never_dropped(string title)
    {
        SequelNumber.For(title).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_title_has_no_sequel_number(string? title)
    {
        SequelNumber.For(title).ShouldBeNull();
    }
}
