using System.Diagnostics.CodeAnalysis;

namespace CollectionMaxxing.Domain.Identifiers;

/// <summary>
/// A validated ISBN-13: 13 digits, a correct check digit, and a Bookland prefix of 978 or 979 followed by 1 to 9.
/// </summary>
/// <remarks>
/// 979-0 numbers are rejected. They share the 979 prefix and a valid EAN-13 check digit, but 9790 is the ISMN range for
/// printed music, not books; accepting them would put sheet music on the book shelf. <see cref="Gtin.Kind"/> reports
/// such a barcode as <see cref="Catalog.WorkKind.Other"/> instead.
/// </remarks>
public sealed record Isbn13
{
    private Isbn13(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// Reads a typed or scanned ISBN-13, or an ISBN-10 (converted). An "ISBN" label, spaces, hyphens and a trailing
    /// EAN add-on are tolerated. Never throws.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out Isbn13? isbn)
    {
        isbn = null;
        var text = BarcodeText.Clean(input);
        if (text is null)
        {
            return false;
        }

        if (text.Length == 10)
        {
            if (!Isbn10.TryParse(text, out var isbn10))
            {
                return false;
            }

            isbn = isbn10.ToIsbn13();
            return true;
        }

        if (text.Length != 13 || !BarcodeText.IsAllDigits(text) || !IsBooklandPrefix(text) || !Gs1CheckDigit.IsValid(text))
        {
            return false;
        }

        isbn = new Isbn13(text);
        return true;
    }

    /// <summary>The same number as a GTIN-13.</summary>
    public Gtin ToGtin() => Gtin.FromValidated13(Value);

    public override string ToString() => Value;

    internal static Isbn13 FromValidated(string value) => new(value);

    internal static bool IsBooklandPrefix(string digits) =>
        digits.StartsWith("978", StringComparison.Ordinal)
        || (digits.StartsWith("979", StringComparison.Ordinal) && digits.Length > 3 && digits[3] != '0');
}
