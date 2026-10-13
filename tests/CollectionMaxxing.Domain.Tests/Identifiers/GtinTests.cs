using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Identifiers;

// Locked acceptance tests for CAT-1 (docs/02 section 2; docs/09 "Domain: identifiers"). Do not weaken, skip or delete.
public class GtinTests
{
    // ---- docs/09 starter list ----------------------------------------------------------------

    [Fact]
    public void Valid_UPC_A_normalises_to_GTIN_13_with_a_leading_zero()
    {
        Gtin.TryParse("036000291452", out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe("0036000291452");
        gtin.ToIdentifier().Type.ShouldBe(IdentifierType.Gtin13);
    }

    [Fact]
    public void UPC_A_with_a_bad_check_digit_is_rejected()
    {
        Gtin.TryParse("036000291453", out var gtin).ShouldBeFalse();

        gtin.ShouldBeNull();
    }

    // One case per UPC-E number-system rule (last digit of the six: 0-2, 3, 4, 5-9) and both number systems.
    [Theory]
    [InlineData("04252614", "042100005264")] // NS 0, ends in 1
    [InlineData("01234505", "012000003455")] // NS 0, ends in 0
    [InlineData("01234523", "012200003453")] // NS 0, ends in 2
    [InlineData("01234531", "012300000451")] // NS 0, ends in 3
    [InlineData("01234543", "012340000053")] // NS 0, ends in 4
    [InlineData("01234565", "012345000065")] // NS 0, ends in 5
    [InlineData("01234596", "012345000096")] // NS 0, ends in 9
    [InlineData("11234502", "112000003452")] // NS 1, ends in 0
    [InlineData("11234538", "112300000458")] // NS 1, ends in 3
    [InlineData("16543270", "165432000070")] // NS 1, ends in 7
    [InlineData("12345687", "123456000087")] // NS 1, ends in 8
    public void UPC_E_expands_to_the_correct_UPC_A(string upcE, string upcA)
    {
        Gtin.TryParseUpcE(upcE, out var fromUpcE).ShouldBeTrue();
        Gtin.TryParse(upcA, out var fromUpcA).ShouldBeTrue();

        fromUpcE.ShouldNotBeNull();
        fromUpcE.Value.ShouldBe("0" + upcA);
        fromUpcE.ShouldBe(fromUpcA);
    }

    [Theory]
    [InlineData("04252615")] // check digit off by one
    [InlineData("01234506")]
    [InlineData("11234503")]
    [InlineData("16543271")]
    [InlineData("12345688")]
    public void UPC_E_with_a_bad_check_digit_is_rejected(string upcE)
    {
        Gtin.TryParseUpcE(upcE, out var gtin).ShouldBeFalse();

        gtin.ShouldBeNull();
    }

    [Theory]
    [InlineData("24252614")] // number system 2 does not exist for UPC-E
    [InlineData("94252614")]
    public void UPC_E_with_a_number_system_other_than_0_or_1_is_rejected(string upcE)
    {
        Gtin.TryParseUpcE(upcE, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_typed_eight_digit_UPC_E_is_recognised_without_being_told_the_symbology()
    {
        Gtin.TryParse("04252614", out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe("0042100005264");
    }

    // ---- table-driven vectors: each valid code has a bad-check-digit twin that is rejected ------

    [Theory]
    [InlineData("036000291452", "036000291453")]
    [InlineData("012345678905", "012345678906")]
    [InlineData("042100005264", "042100005265")]
    [InlineData("072030131122", "072030131123")]
    [InlineData("012000001253", "012000001254")]
    [InlineData("085493041112", "085493041110")]
    public void UPC_A_vectors_are_accepted_and_their_bad_check_digit_twins_are_rejected(string valid, string bad)
    {
        Gtin.TryParse(valid, out var gtin).ShouldBeTrue();
        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe("0" + valid);

        Gtin.TryParse(bad, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("4006381333931", "4006381333932")]
    [InlineData("5901234123457", "5901234123458")]
    [InlineData("5012345678900", "5012345678901")]
    [InlineData("4000000000006", "4000000000007")]
    [InlineData("3017620422003", "3017620422004")]
    public void EAN_13_vectors_are_accepted_and_their_bad_check_digit_twins_are_rejected(string valid, string bad)
    {
        Gtin.TryParse(valid, out var gtin).ShouldBeTrue();
        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe(valid);

        Gtin.TryParse(bad, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("73513537", "73513538")]
    [InlineData("96385074", "96385075")]
    [InlineData("12345670", "12345671")]
    [InlineData("55123457", "55123458")]
    public void EAN_8_vectors_are_accepted_as_is_and_their_bad_check_digit_twins_are_rejected(string valid, string bad)
    {
        Gtin.TryParseEan8(valid, out var gtin).ShouldBeTrue();
        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe(valid);
        gtin.ToIdentifier().Type.ShouldBe(IdentifierType.Gtin8);

        Gtin.TryParseEan8(bad, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("73513537")]
    [InlineData("96385074")]
    [InlineData("12345670")]
    public void An_eight_digit_code_that_is_not_a_UPC_E_is_read_as_an_EAN_8(string ean8)
    {
        Gtin.TryParse(ean8, out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe(ean8);
    }

    // ---- add-ons, whitespace, hyphens, junk ---------------------------------------------------

    [Theory]
    [InlineData("9780306406157 12345")]   // scanner output with a space before the add-on
    [InlineData("978030640615712345")]    // run-on 5-digit add-on
    [InlineData("978030640615734")]       // run-on 2-digit add-on
    [InlineData("9780306406157 34")]
    public void Five_digit_EAN_add_ons_are_ignored_by_the_classifier(string withAddOn)
    {
        Gtin.TryParse(withAddOn, out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe("9780306406157");
        gtin.Kind.ShouldBe(WorkKind.Book);
    }

    [Theory]
    [InlineData("036000291452 12345", "0036000291452")]
    [InlineData("03600029145254321", "0036000291452")]
    public void Add_ons_are_ignored_on_UPC_A_too(string withAddOn, string expected)
    {
        Gtin.TryParse(withAddOn, out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("  036000291452  ")]
    [InlineData("0-36000-29145-2")]
    [InlineData("036 000 291 452")]
    [InlineData("\t036000291452\n")]
    public void Whitespace_and_hyphens_around_and_inside_a_barcode_are_tolerated(string typed)
    {
        Gtin.TryParse(typed, out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Value.ShouldBe("0036000291452");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("03600029145X")]
    [InlineData("0360002914")]
    [InlineData("0360002914521234567890123")]
    [InlineData("０３６０００２９１４５２")] // full-width digits are not barcode digits
    [InlineData("03600029145٢")]       // Arabic-Indic digit
    public void Garbage_is_rejected_without_throwing(string? junk)
    {
        Gtin.TryParse(junk, out var gtin).ShouldBeFalse();

        gtin.ShouldBeNull();
    }

    // ---- classification ----------------------------------------------------------------------

    [Fact]
    public void A_plain_product_GTIN_has_no_kind_until_a_lookup_says_what_it_is()
    {
        Gtin.TryParse("4006381333931", out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Kind.ShouldBeNull();
        gtin.ToIdentifier().ShouldBe(new Identifier(IdentifierType.Gtin13, "4006381333931"));
    }

    [Fact]
    public void An_EAN_13_with_prefix_978_is_classified_as_an_ISBN_13_book()
    {
        Gtin.TryParse("9780306406157", out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Kind.ShouldBe(WorkKind.Book);
        gtin.ToIdentifier().ShouldBe(new Identifier(IdentifierType.Isbn13, "9780306406157"));
        gtin.ToIsbn13().ShouldNotBeNull().Value.ShouldBe("9780306406157");
    }

    [Theory]
    [InlineData("9791000000015")] // 979-10 (France)
    [InlineData("9798265172143")] // 979-8 (United States)
    [InlineData("9791109101729")] // 979-11 (Korea)
    public void An_EAN_13_with_prefix_979_1_to_979_9_is_classified_as_an_ISBN_13_book(string ean)
    {
        Gtin.TryParse(ean, out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Kind.ShouldBe(WorkKind.Book);
        gtin.ToIdentifier().Type.ShouldBe(IdentifierType.Isbn13);
    }

    [Fact]
    public void An_EAN_13_with_prefix_9790_is_classified_as_ISMN_and_never_as_a_book()
    {
        Gtin.TryParse("9790000000124", out var gtin).ShouldBeTrue();

        gtin.ShouldNotBeNull();
        gtin.Kind.ShouldBe(WorkKind.Other);
        gtin.ToIdentifier().Type.ShouldBe(IdentifierType.Gtin13);
        gtin.ToIsbn13().ShouldBeNull();
    }

    // ---- properties --------------------------------------------------------------------------

    [Fact]
    public void Property_any_valid_UPC_A_round_trips_through_GTIN_13_and_back()
    {
        var random = new Random(20261013);
        for (var i = 0; i < 2000; i++)
        {
            var upcA = WithCheckDigit(RandomDigits(random, 11));

            Gtin.TryParse(upcA, out var gtin).ShouldBeTrue(upcA);
            gtin.ShouldNotBeNull();
            gtin.Value.Length.ShouldBe(13);
            gtin.Value[0].ShouldBe('0');
            var back = gtin.ToUpcA();
            back.ShouldBe(upcA);
            Gtin.TryParse(back, out var again).ShouldBeTrue();
            again.ShouldBe(gtin);
        }
    }

    [Fact]
    public void Property_changing_any_single_digit_of_a_valid_UPC_A_breaks_the_check_digit()
    {
        var random = new Random(1013);
        for (var i = 0; i < 300; i++)
        {
            var upcA = WithCheckDigit(RandomDigits(random, 11));

            for (var position = 0; position < upcA.Length; position++)
            {
                for (var digit = '0'; digit <= '9'; digit++)
                {
                    if (digit == upcA[position])
                    {
                        continue;
                    }

                    var changed = upcA.Remove(position, 1).Insert(position, digit.ToString());
                    Gtin.TryParse(changed, out _).ShouldBeFalse($"{upcA} -> {changed}");
                }
            }
        }
    }

    [Fact]
    public void Property_changing_any_single_digit_of_a_valid_EAN_13_breaks_the_check_digit()
    {
        var random = new Random(77);
        for (var i = 0; i < 300; i++)
        {
            var ean = WithCheckDigit("40" + RandomDigits(random, 10));

            for (var position = 0; position < ean.Length; position++)
            {
                for (var digit = '0'; digit <= '9'; digit++)
                {
                    if (digit == ean[position])
                    {
                        continue;
                    }

                    var changed = ean.Remove(position, 1).Insert(position, digit.ToString());
                    Gtin.TryParse(changed, out _).ShouldBeFalse($"{ean} -> {changed}");
                }
            }
        }
    }

    private static string RandomDigits(Random random, int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + random.Next(10))));

    // An independent implementation of the GS1 check digit, so the tests do not trust the code under test.
    private static string WithCheckDigit(string body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var fromRight = body.Length - 1 - i;
            sum += (body[i] - '0') * (fromRight % 2 == 0 ? 3 : 1);
        }

        return body + (char)('0' + ((10 - (sum % 10)) % 10));
    }
}
