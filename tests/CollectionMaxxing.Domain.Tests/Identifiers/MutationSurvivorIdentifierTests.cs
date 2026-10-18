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
