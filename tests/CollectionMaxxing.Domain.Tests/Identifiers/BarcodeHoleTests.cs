using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Identifiers;

// Locked acceptance tests for CAT-1b (issue #48): barcode parsing must never turn one product's code into another's.
// Do not weaken, skip or delete. They sit beside the CAT-1 tests in GtinTests and IsbnTests.
public class BarcodeHoleTests
{
    // ---- 5. GTIN-14 --------------------------------------------------------------------------

    private static string WithCheckDigit(string body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++)
        {
            sum += (body[i] - '0') * ((body.Length - 1 - i) % 2 == 0 ? 3 : 1);
        }

        return body + (char)('0' + ((10 - (sum % 10)) % 10));
    }

    private static string RandomDigits(Random random, int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)('0' + random.Next(10))));

    [Fact]
    public void A_valid_GTIN_14_with_a_packaging_indicator_of_1_to_8_never_parses_as_a_UPC_A()
    {
        var random = new Random(14);
        for (var i = 0; i < 500; i++)
        {
            var gtin14 = WithCheckDigit($"{random.Next(1, 9)}{RandomDigits(random, 12)}");
            gtin14.Length.ShouldBe(14);

            Gtin.TryParse(gtin14, out var gtin).ShouldBeFalse(gtin14);
            gtin.ShouldBeNull();
            Identifier.TryParse(gtin14, out var identifier).ShouldBeFalse(gtin14);
            identifier.ShouldBeNull();
        }
    }

    [Fact]
    public void A_GTIN_14_is_not_read_through_spaces_or_hyphens_either()
    {
        Gtin.TryParse("10036000291452", out _).ShouldBeFalse();
        Gtin.TryParse("1-0036000-29145-2", out _).ShouldBeFalse();
        Gtin.TryParse(" 1003 6000 2914 52 ", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_14_digit_code_starting_with_0_and_a_valid_check_digit_is_the_GTIN_13()
    {
        Gtin.TryParse("00036000291452", out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe("0036000291452");
        Identifier.TryParse("00036000291452", out var identifier).ShouldBeTrue();
        identifier.ShouldNotBeNull();
        identifier.Value.ShouldBe("0036000291452");
    }

    [Fact]
    public void A_14_digit_code_starting_with_0_is_the_ISBN_13_when_the_rest_is_Bookland()
    {
        Gtin.TryParse("09780306406157", out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe("9780306406157");
        gtin.ToIsbn13().ShouldNotBeNull();
    }

    [Fact]
    public void A_14_digit_code_starting_with_0_and_a_bad_check_digit_is_rejected()
    {
        Gtin.TryParse("00036000291453", out var gtin).ShouldBeFalse();
        gtin.ShouldBeNull();
    }

    [Fact]
    public void A_14_digit_code_starting_with_9_is_rejected()
    {
        // 9 is the variable-measure indicator; those are not retail product barcodes either.
        Gtin.TryParse(WithCheckDigit("9036000291452"), out _).ShouldBeFalse();
    }

    [Fact]
    public void The_other_run_on_add_ons_are_still_ignored()
    {
        Gtin.TryParse("03600029145254321", out var upcWithFive).ShouldBeTrue();
        upcWithFive.ShouldNotBeNull();
        upcWithFive.Value.ShouldBe("0036000291452");

        Gtin.TryParse("978030640615734", out var eanWithTwo).ShouldBeTrue();
        eanWithTwo.ShouldNotBeNull();
        eanWithTwo.Value.ShouldBe("9780306406157");
    }

    // ---- 6. spaced ISBN-10 -------------------------------------------------------------------

    [Theory]
    [InlineData("03064061 52")]
    [InlineData("0-306-40615-2")]
    [InlineData("0306406152")]
    [InlineData("ISBN 03064061 52")]
    public void An_8_plus_2_split_that_is_also_a_valid_ISBN_10_is_read_as_the_ISBN_10(string typed)
    {
        Isbn10.TryParse(typed, out var isbn10).ShouldBeTrue();

        isbn10.ShouldNotBeNull();
        isbn10.Value.ShouldBe("0306406152");
    }

    [Fact]
    public void The_spaced_ISBN_10_converts_to_the_ISBN_13_and_identifier()
    {
        Isbn13.TryParse("03064061 52", out var isbn13).ShouldBeTrue();
        isbn13.ShouldNotBeNull();
        isbn13.Value.ShouldBe("9780306406157");

        Identifier.TryParse("03064061 52", out var identifier).ShouldBeTrue();
        identifier.ShouldNotBeNull();
        identifier.Type.ShouldBe(IdentifierType.Isbn13);
        identifier.Value.ShouldBe("9780306406157");
    }

    [Fact]
    public void An_8_plus_2_split_that_is_not_a_valid_ISBN_10_is_still_a_code_with_an_add_on()
    {
        // 1234567034 fails the ISBN-10 check, so "12345670 34" stays an 8 digit code with a 2 digit add-on.
        Isbn10.TryParse("12345670 34", out _).ShouldBeFalse();
        Gtin.TryParse("12345670 34", out var gtin).ShouldBeTrue();
        gtin.ShouldNotBeNull();
        gtin.Value.Length.ShouldBeInRange(8, 13);
    }
}
