using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Identifiers;

// Locked acceptance tests for CAT-1 (docs/02 section 2). Do not weaken, skip or delete.
public class IdentifierTests
{
    [Theory]
    [InlineData("036000291452", IdentifierType.Gtin13, "0036000291452", null)]
    [InlineData("04252614", IdentifierType.Gtin13, "0042100005264", null)]
    [InlineData("4006381333931", IdentifierType.Gtin13, "4006381333931", null)]
    [InlineData("73513537", IdentifierType.Gtin8, "73513537", null)]
    [InlineData("9780306406157", IdentifierType.Isbn13, "9780306406157", WorkKind.Book)]
    [InlineData("9798265172143", IdentifierType.Isbn13, "9798265172143", WorkKind.Book)]
    [InlineData("0306406152", IdentifierType.Isbn13, "9780306406157", WorkKind.Book)]
    [InlineData("080442957X", IdentifierType.Isbn13, "9780804429573", WorkKind.Book)]
    [InlineData("978-0-306-40615-7 12345", IdentifierType.Isbn13, "9780306406157", WorkKind.Book)]
    [InlineData("9790000000124", IdentifierType.Gtin13, "9790000000124", WorkKind.Other)]
    public void A_scanned_or_typed_code_becomes_the_right_identifier_and_kind(
        string raw, IdentifierType type, string value, WorkKind? kind)
    {
        Identifier.TryParse(raw, out var id).ShouldBeTrue();

        id.ShouldNotBeNull();
        id.Type.ShouldBe(type);
        id.Value.ShouldBe(value);
        id.ProbableKind.ShouldBe(kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("036000291453")]
    [InlineData("0306406153")]
    [InlineData("hello")]
    public void A_bad_code_is_rejected_without_throwing(string? raw)
    {
        Identifier.TryParse(raw, out var id).ShouldBeFalse();

        id.ShouldBeNull();
    }

    [Fact]
    public void Identifiers_with_the_same_type_and_value_are_equal()
    {
        new Identifier(IdentifierType.Gtin13, "4006381333931")
            .ShouldBe(new Identifier(IdentifierType.Gtin13, "4006381333931"));
        new Identifier(IdentifierType.Gtin13, "4006381333931")
            .ShouldNotBe(new Identifier(IdentifierType.Gtin13, "5901234123457"));
    }

    [Fact]
    public void An_identifier_is_validated_on_construction()
    {
        Should.Throw<ArgumentException>(() => new Identifier(IdentifierType.Gtin13, "4006381333932"));
        Should.Throw<ArgumentException>(() => new Identifier(IdentifierType.Isbn13, "4006381333931"));
        Should.Throw<ArgumentException>(() => new Identifier(IdentifierType.Gtin8, "73513538"));
    }

    [Fact]
    public void TryCreate_never_throws_on_bad_input()
    {
        Identifier.TryCreate(IdentifierType.Gtin13, "nope", out var bad).ShouldBeFalse();
        bad.ShouldBeNull();

        Identifier.TryCreate(IdentifierType.Isbn13, "9780306406157", out var good).ShouldBeTrue();
        good.ShouldBe(new Identifier(IdentifierType.Isbn13, "9780306406157"));
    }

    [Fact]
    public void An_ASIN_is_ten_upper_case_letters_or_digits()
    {
        Identifier.TryCreate(IdentifierType.Asin, "B000FBJCJE", out var asin).ShouldBeTrue();
        asin.ShouldNotBeNull();
        asin.ProbableKind.ShouldBeNull();

        Identifier.TryCreate(IdentifierType.Asin, "b000fbjcje", out _).ShouldBeFalse();
        Identifier.TryCreate(IdentifierType.Asin, "B000", out _).ShouldBeFalse();
    }
}
