using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Identifiers;

// Locked acceptance tests for CAT-1 (docs/02 section 2; docs/09 "Domain: identifiers"). Do not weaken, skip or delete.
public class IsbnTests
{
    [Fact]
    public void ISBN_10_with_an_X_check_digit_converts_to_the_right_ISBN_13()
    {
        Isbn10.TryParse("080442957X", out var isbn10).ShouldBeTrue();

        isbn10.ShouldNotBeNull();
        isbn10.Value.ShouldBe("080442957X");
        isbn10.ToIsbn13().Value.ShouldBe("9780804429573");
    }

    [Fact]
    public void A_lower_case_x_check_digit_is_accepted()
    {
        Isbn10.TryParse("080442957x", out var isbn10).ShouldBeTrue();

        isbn10.ShouldNotBeNull();
        isbn10.Value.ShouldBe("080442957X");
    }

    [Theory]
    [InlineData("  9780306406157  ")]
    [InlineData("\t9780306406157\n")]
    [InlineData("978-0-306-40615-7")]
    [InlineData(" 978-0-306-40615-7 ")]
    [InlineData("978 0 306 40615 7")]
    [InlineData("ISBN 978-0-306-40615-7")]
    [InlineData("ISBN-13: 978-0-306-40615-7")]
    public void Leading_and_trailing_whitespace_and_hyphens_in_typed_ISBNs_are_tolerated(string typed)
    {
        Isbn13.TryParse(typed, out var isbn).ShouldBeTrue();

        isbn.ShouldNotBeNull();
        isbn.Value.ShouldBe("9780306406157");
    }

    [Theory]
    [InlineData(" 0-306-40615-2 ", "9780306406157")]
    [InlineData("0 306 40615 2", "9780306406157")]
    [InlineData("ISBN 0-8044-2957-X", "9780804429573")]
    public void Typed_ISBN_10s_are_tolerated_and_become_ISBN_13s(string typed, string expected)
    {
        Isbn13.TryParse(typed, out var isbn).ShouldBeTrue();

        isbn.ShouldNotBeNull();
        isbn.Value.ShouldBe(expected);
    }

    // Each valid ISBN has a bad-check-digit twin that is rejected.
    [Theory]
    [InlineData("0306406152", "0306406153", "9780306406157")]
    [InlineData("0451524934", "0451524935", "9780451524935")]
    [InlineData("080442957X", "0804429570", "9780804429573")]
    [InlineData("0316062642", "0316062643", "9780316062640")]
    [InlineData("0596520689", "0596520688", "9780596520687")]
    [InlineData("0143127551", "0143127552", "9780143127550")]
    public void ISBN_10_vectors_convert_to_ISBN_13_and_their_bad_check_digit_twins_are_rejected(
        string valid, string bad, string expected13)
    {
        Isbn10.TryParse(valid, out var isbn10).ShouldBeTrue();
        isbn10.ShouldNotBeNull();
        isbn10.ToIsbn13().Value.ShouldBe(expected13);

        Isbn10.TryParse(bad, out var rejected).ShouldBeFalse();
        rejected.ShouldBeNull();
    }

    [Theory]
    [InlineData("9780306406157", "9780306406158")]
    [InlineData("9781593275846", "9781593275847")]
    [InlineData("9791000000015", "9791000000016")]   // 979-10
    [InlineData("9798265172143", "9798265172144")]   // 979-8
    [InlineData("9780143127550", "9780143127551")]
    [InlineData("9780451524935", "9780451524936")]
    public void ISBN_13_vectors_are_accepted_and_their_bad_check_digit_twins_are_rejected(string valid, string bad)
    {
        Isbn13.TryParse(valid, out var isbn).ShouldBeTrue();
        isbn.ShouldNotBeNull();
        isbn.Value.ShouldBe(valid);

        Isbn13.TryParse(bad, out var rejected).ShouldBeFalse();
        rejected.ShouldBeNull();
    }

    // 9790 is ISMN (printed music), never a book. 978 and 979-1 to 979-9 always are. We reject 979-0 from Isbn13 rather
    // than accepting it as a book: a 979-0 number has a valid EAN check digit but is not an ISBN, and silently treating
    // sheet music as a book would put it on the wrong shelf. Gtin.Kind reports it as WorkKind.Other instead.
    [Fact]
    public void A_979_0_number_is_rejected_as_not_a_book()
    {
        Isbn13.TryParse("9790000000124", out var isbn).ShouldBeFalse();

        isbn.ShouldBeNull();
    }

    [Theory]
    [InlineData("9780306406157")]
    [InlineData("9791000000015")]
    [InlineData("9798265172143")]
    [InlineData("9791109101729")]
    public void Prefixes_978_and_979_1_to_9_are_always_books(string value)
    {
        Isbn13.TryParse(value, out var isbn).ShouldBeTrue();

        isbn.ShouldNotBeNull();
        isbn.ToGtin().Kind.ShouldBe(WorkKind.Book);
    }

    [Theory]
    [InlineData("4006381333931")] // a valid EAN-13 that is not Bookland
    [InlineData("5901234123457")]
    public void A_valid_EAN_13_outside_Bookland_is_not_an_ISBN(string ean)
    {
        Isbn13.TryParse(ean, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("978")]
    [InlineData("97803064061")]
    [InlineData("978030640615X")]
    [InlineData("X306406152")]
    [InlineData("03064061X2")]
    [InlineData("not an isbn")]
    public void Garbage_is_rejected_without_throwing(string? junk)
    {
        Isbn13.TryParse(junk, out var isbn13).ShouldBeFalse();
        Isbn10.TryParse(junk, out var isbn10).ShouldBeFalse();

        isbn13.ShouldBeNull();
        isbn10.ShouldBeNull();
    }

    [Fact]
    public void Property_every_valid_ISBN_10_converts_to_a_valid_ISBN_13_that_parses_again()
    {
        var random = new Random(978);
        for (var i = 0; i < 1000; i++)
        {
            var body = string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + random.Next(10))));
            var sum = 0;
            for (var d = 0; d < 9; d++)
            {
                sum += (body[d] - '0') * (10 - d);
            }

            var check = (11 - (sum % 11)) % 11;
            var isbn10 = body + (check == 10 ? 'X' : (char)('0' + check));

            Isbn10.TryParse(isbn10, out var parsed).ShouldBeTrue(isbn10);
            parsed.ShouldNotBeNull();
            var isbn13 = parsed.ToIsbn13();
            Isbn13.TryParse(isbn13.Value, out var again).ShouldBeTrue(isbn10);
            again.ShouldBe(isbn13);
            Isbn13.TryParse(isbn10, out var fromTen).ShouldBeTrue();
            fromTen.ShouldBe(isbn13);
        }
    }
}
