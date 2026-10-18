using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Identifiers;

// Added by QA-3b: behaviours a mutation sweep showed nothing was checking.
public class MutationSurvivorIdentifierTests
{
    [Fact]
    public void A_run_on_add_on_is_only_trimmed_when_every_character_is_a_digit()
    {
        // "9780306406157" plus a 5 character add-on; with a letter in it the text is not a run of digits, so it is not
        // trimmed down to a valid ISBN-13.
        Gtin.TryParse("97803064061571234X", out _).ShouldBeFalse();
        Gtin.TryParse("978030640615712345", out var trimmed).ShouldBeTrue();
        trimmed!.Value.ShouldBe("9780306406157");
    }

    [Theory]
    [InlineData("0123456X")]
    [InlineData("0123456789")]
    [InlineData("012345")]
    public void A_text_that_is_not_8_digits_is_not_a_UPC_E(string text)
    {
        Gtin.TryParseUpcE(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_12_digit_text_with_a_valid_check_digit_is_not_an_ISBN_13()
    {
        Isbn13.TryParse("978030640612", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_7_digit_text_with_a_valid_check_digit_is_not_an_EAN_8()
    {
        Gtin.TryParseEan8("0000000", out _).ShouldBeFalse();
    }

    [Fact]
    public void An_identifier_type_that_does_not_exist_cannot_be_created()
    {
        Identifier.TryCreate((IdentifierType)999, "anything", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_13_digit_code_that_does_not_start_with_0_has_no_UPC_A_form()
    {
        Gtin.TryParse("4006381333931", out var gtin).ShouldBeTrue();

        gtin!.ToUpcA().ShouldBeNull();
    }

    [Fact]
    public void An_EAN_8_has_no_UPC_A_form()
    {
        Gtin.TryParseEan8("73513537", out var gtin).ShouldBeTrue();

        gtin!.ToUpcA().ShouldBeNull();
    }
}
