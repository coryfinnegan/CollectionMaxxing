using CollectionMaxxing.Application.Scanning.Identify;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A cover often prints a possessive name in front of its title ("Walt Disney's", "Steven Spielberg's"), sometimes with a marketing
// word after it ("Masterpiece", "Classic"), and the model reads the lot as the title. This is which words count as that prefix,
// and what is left of the title without it. The names are the public ones the covers print, or neutral.
public class PossessivePrefixTests
{
    [Theory]
    [InlineData("WALT DISNEY'S MASTERPIECE PINOCCHIO", "PINOCCHIO", "WALT DISNEY'S MASTERPIECE")] // the cover that started it
    [InlineData("Walt Disney's Pinocchio", "Pinocchio", "Walt Disney's")]
    [InlineData("DISNEY'S ALADDIN", "ALADDIN", "DISNEY'S")] // one word of name
    [InlineData("STEVEN SPIELBERG'S JURASSIC PARK", "JURASSIC PARK", "STEVEN SPIELBERG'S")]
    [InlineData("STEVEN SPIELBERG’S JURASSIC PARK", "JURASSIC PARK", "STEVEN SPIELBERG’S")] // a curly apostrophe is the same
    [InlineData("TESS ANN HARROWGATE'S COPPER LANTERN", "COPPER LANTERN", "TESS ANN HARROWGATE'S")] // three words of name
    [InlineData("Jim Henson's Classic Labyrinth", "Labyrinth", "Jim Henson's Classic")]
    [InlineData("WALT DISNEY'S MASTERPIECES PINOCCHIO", "PINOCCHIO", "WALT DISNEY'S MASTERPIECES")]
    [InlineData("WALT DISNEY'S CLASSICS PINOCCHIO", "PINOCCHIO", "WALT DISNEY'S CLASSICS")]
    [InlineData("DISNEY'S PRESENTS PINOCCHIO", "PINOCCHIO", "DISNEY'S PRESENTS")]
    [InlineData("DISNEY'S PRESENT PINOCCHIO", "PINOCCHIO", "DISNEY'S PRESENT")]
    [InlineData("DISNEY'S COLLECTION PINOCCHIO", "PINOCCHIO", "DISNEY'S COLLECTION")]
    [InlineData("DISNEY'S ORIGINAL PINOCCHIO", "PINOCCHIO", "DISNEY'S ORIGINAL")]
    [InlineData("DISNEY'S SPECIAL PINOCCHIO", "PINOCCHIO", "DISNEY'S SPECIAL")]
    [InlineData("DISNEY'S masterpiece PINOCCHIO", "PINOCCHIO", "DISNEY'S masterpiece")] // the marketing word in any case
    [InlineData("DISNEY'S MASTERPIECE: PINOCCHIO", "PINOCCHIO", "DISNEY'S MASTERPIECE:")] // punctuation goes with the word
    [InlineData("  WALT   DISNEY'S   PINOCCHIO  ", "PINOCCHIO", "WALT DISNEY'S")] // spacing is only spacing
    [InlineData("DISNEY'S MASTERPIECE COLLECTION PINOCCHIO", "COLLECTION PINOCCHIO", "DISNEY'S MASTERPIECE")] // one marketing word, not two
    [InlineData("ENDER'S GAME", "GAME", "ENDER'S")] // a possessive that is the real title: one word is left, and the caller keeps the original
    [InlineData("BRAM STOKER'S DRACULA", "DRACULA", "BRAM STOKER'S")]
    [InlineData("Stephen King's It", "It", "Stephen King's")]
    public void A_possessive_name_and_one_marketing_word_after_it_are_dropped_from_the_front_of_a_title(string title, string left, string dropped)
    {
        var drop = PossessivePrefix.For(title);

        drop.ShouldNotBeNull();
        drop.Title.ShouldBe(left);
        drop.Dropped.ShouldBe(dropped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PINOCCHIO")]
    [InlineData("WALT DISNEY PINOCCHIO")] // no possessive
    [InlineData("WALT DISNEY'S")] // nothing left after the name
    [InlineData("DISNEY'S MASTERPIECE")] // a marketing word is no title
    [InlineData("DISNEY'S MASTERPIECES")]
    [InlineData("ONE TWO THREE FOUR'S PINOCCHIO")] // a name is at most three words
    [InlineData("walt disney's masterpiece pinocchio")] // a name is capitalised
    [InlineData("Walt disney's Pinocchio")]
    [InlineData("THE KING'S SPEECH")] // an article is no name: "the king's" is a thing, not a person
    [InlineData("A BUG'S LIFE")]
    [InlineData("An Angel's Tale")]
    [InlineData("'S PINOCCHIO")] // an apostrophe and an s are no name
    [InlineData("DISNEY'S ")] // and no words after it
    [InlineData("JAMES' PINOCCHIO")] // only 's and ’s
    [InlineData("1984'S PINOCCHIO")] // a name starts with a letter
    [InlineData("PINOCCHIO DISNEY'S")] // the prefix is at the front
    public void A_title_without_a_possessive_name_at_its_front_with_a_word_after_it_is_left_alone(string? title)
    {
        PossessivePrefix.For(title).ShouldBeNull();
    }

    [Fact]
    public void The_first_possessive_word_ends_the_name()
    {
        var drop = PossessivePrefix.For("TESS HARROWGATE'S ANN'S PINOCCHIO");

        drop.ShouldNotBeNull();
        drop.Title.ShouldBe("ANN'S PINOCCHIO");
        drop.Dropped.ShouldBe("TESS HARROWGATE'S");
    }
}
