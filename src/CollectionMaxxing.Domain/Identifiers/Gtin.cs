using System.Diagnostics.CodeAnalysis;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Domain.Identifiers;

/// <summary>
/// A validated product barcode, normalised to GTIN-13 (UPC-A gets a leading zero, UPC-E is expanded, EAN-13 is kept),
/// or GTIN-8 for an EAN-8 (docs/02 section 2). Immutable; construct with <see cref="TryParse"/>.
/// </summary>
public sealed record Gtin
{
    private Gtin(string value) => Value = value;

    /// <summary>13 digits, or 8 for an EAN-8.</summary>
    public string Value { get; }

    /// <summary>
    /// What the number alone says it is: <see cref="WorkKind.Book"/> for Bookland (978, or 979 followed by 1 to 9),
    /// <see cref="WorkKind.Other"/> for ISMN sheet music (9790), and null for an ordinary product (movie, game or other,
    /// which only a lookup can tell).
    /// </summary>
    public WorkKind? Kind
    {
        get
        {
            if (Value.Length != 13)
            {
                return null;
            }

            if (Value.StartsWith("9790", StringComparison.Ordinal))
            {
                return WorkKind.Other;
            }

            return Value.StartsWith("978", StringComparison.Ordinal) || Value.StartsWith("979", StringComparison.Ordinal)
                ? WorkKind.Book
                : null;
        }
    }

    /// <summary>
    /// Reads a scanned or typed barcode: UPC-A (12 digits), UPC-E or EAN-8 (8 digits), or EAN-13, with spaces, hyphens
    /// and 2 or 5 digit add-ons tolerated. Never throws.
    /// </summary>
    /// <remarks>
    /// An 8 digit code is ambiguous without knowing the symbology (UPC-E or EAN-8). When the first digit is 0 or 1 and
    /// the code is a valid UPC-E it is read as UPC-E; otherwise as EAN-8. Callers that know the symbology should use
    /// <see cref="TryParseUpcE"/> or <see cref="TryParseEan8"/>.
    /// </remarks>
    public static bool TryParse(string? input, [NotNullWhen(true)] out Gtin? gtin)
    {
        gtin = null;
        var text = BarcodeText.Clean(input);
        if (text is null || !BarcodeText.IsAllDigits(text))
        {
            return false;
        }

        switch (text.Length)
        {
            case 12 when Gs1CheckDigit.IsValid(text):
                gtin = new Gtin("0" + text);
                return true;
            case 13 when Gs1CheckDigit.IsValid(text):
                gtin = new Gtin(text);
                return true;
            case 8:
                if (TryExpandUpcE(text, out var expanded))
                {
                    gtin = new Gtin("0" + expanded);
                    return true;
                }

                if (Gs1CheckDigit.IsValid(text))
                {
                    gtin = new Gtin(text);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Reads an 8 digit UPC-E (number system 0 or 1) and expands it to a GTIN-13. Never throws.</summary>
    public static bool TryParseUpcE(string? input, [NotNullWhen(true)] out Gtin? gtin)
    {
        gtin = null;
        var text = BarcodeText.Clean(input);
        if (text is null || !TryExpandUpcE(text, out var expanded))
        {
            return false;
        }

        gtin = new Gtin("0" + expanded);
        return true;
    }

    /// <summary>Reads an 8 digit EAN-8 and keeps it as a GTIN-8. Never throws.</summary>
    public static bool TryParseEan8(string? input, [NotNullWhen(true)] out Gtin? gtin)
    {
        gtin = null;
        var text = BarcodeText.Clean(input);
        if (text is null || text.Length != 8 || !BarcodeText.IsAllDigits(text) || !Gs1CheckDigit.IsValid(text))
        {
            return false;
        }

        gtin = new Gtin(text);
        return true;
    }

    /// <summary>The 12 digit UPC-A form, or null when this is not a GTIN-13 that starts with 0.</summary>
    public string? ToUpcA() => Value.Length == 13 && Value[0] == '0' ? Value[1..] : null;

    /// <summary>The ISBN-13 when this is a Bookland number, otherwise null (9790 ISMN is not a book).</summary>
    public Isbn13? ToIsbn13() => Kind == WorkKind.Book ? Isbn13.FromValidated(Value) : null;

    /// <summary>The catalogue identifier for this barcode: an ISBN-13 for books, otherwise a GTIN-13 or GTIN-8.</summary>
    public Identifier ToIdentifier() => new(
        Kind == WorkKind.Book ? IdentifierType.Isbn13 : Value.Length == 8 ? IdentifierType.Gtin8 : IdentifierType.Gtin13,
        Value);

    public override string ToString() => Value;

    internal static Gtin FromValidated13(string value) => new(value);

    // UPC-E to UPC-A, by the last of the six digits that follow the number system (GS1 General Specifications):
    //   0, 1, 2:  NS m1 m2 d6 0000 d3 d4 d5   (the manufacturer digits are m1 m2 d6)
    //   3:        NS m1 m2 m3 00000 d4 d5
    //   4:        NS m1 m2 m3 m4 00000 d5
    //   5 to 9:   NS m1 m2 m3 m4 m5 0000 d6
    // The UPC-E check digit is the check digit of the expanded UPC-A.
    private static bool TryExpandUpcE(string text, [NotNullWhen(true)] out string? upcA)
    {
        upcA = null;
        if (text.Length != 8 || !BarcodeText.IsAllDigits(text) || text[0] is not ('0' or '1'))
        {
            return false;
        }

        var ns = text[0];
        var d = text.AsSpan(1, 6);
        var body = d[5] switch
        {
            '0' or '1' or '2' => $"{ns}{d[0]}{d[1]}{d[5]}0000{d[2]}{d[3]}{d[4]}",
            '3' => $"{ns}{d[0]}{d[1]}{d[2]}00000{d[3]}{d[4]}",
            '4' => $"{ns}{d[0]}{d[1]}{d[2]}{d[3]}00000{d[4]}",
            _ => $"{ns}{d[0]}{d[1]}{d[2]}{d[3]}{d[4]}0000{d[5]}",
        };

        if (Gs1CheckDigit.For(body) != text[7])
        {
            return false;
        }

        upcA = body + text[7];
        return true;
    }
}
