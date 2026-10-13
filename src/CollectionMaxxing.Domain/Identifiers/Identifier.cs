using System.Diagnostics.CodeAnalysis;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Domain.Identifiers;

/// <summary>
/// A barcode or catalogue number of a release, in its canonical form: an ISBN-13 (never a bare GTIN-13 in Bookland), a
/// GTIN-13, a GTIN-8 or an ASIN. Validated on construction; use <see cref="TryParse"/> or <see cref="TryCreate"/> for
/// input you do not trust.
/// </summary>
public sealed record Identifier
{
    /// <summary>Creates an identifier from a value that must already be canonical and valid.</summary>
    /// <exception cref="ArgumentException">The value is not a valid, canonical identifier of that type.</exception>
    public Identifier(IdentifierType type, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValid(type, value))
        {
            throw new ArgumentException($"'{value}' is not a valid {type}.", nameof(value));
        }

        Type = type;
        Value = value;
    }

    public IdentifierType Type { get; }

    public string Value { get; }

    /// <summary>
    /// What the number alone says: a book for an ISBN-13, <see cref="WorkKind.Other"/> for an ISMN (9790), and null for
    /// everything else (a product barcode only a lookup can classify).
    /// </summary>
    public WorkKind? ProbableKind => Type switch
    {
        IdentifierType.Isbn13 => WorkKind.Book,
        IdentifierType.Gtin13 when Value.StartsWith("9790", StringComparison.Ordinal) => WorkKind.Other,
        _ => null,
    };

    /// <summary>Creates an identifier from a canonical value, or returns false. Never throws.</summary>
    public static bool TryCreate(IdentifierType type, string? value, [NotNullWhen(true)] out Identifier? identifier)
    {
        identifier = value is not null && IsValid(type, value) ? new Identifier(type, value) : null;
        return identifier is not null;
    }

    /// <summary>
    /// Reads a scanned or typed code (UPC-A, UPC-E, EAN-8, EAN-13, ISBN-10 or ISBN-13) into its canonical identifier.
    /// Never throws.
    /// </summary>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out Identifier? identifier)
    {
        identifier = null;
        var text = BarcodeText.Clean(raw);
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

            identifier = new Identifier(IdentifierType.Isbn13, isbn10.ToIsbn13().Value);
            return true;
        }

        if (!Gtin.TryParse(text, out var gtin))
        {
            return false;
        }

        identifier = gtin.ToIdentifier();
        return true;
    }

    public override string ToString() => $"{Type}:{Value}";

    private static bool IsValid(IdentifierType type, string value) => type switch
    {
        IdentifierType.Isbn13 => value.Length == 13 && Isbn13.TryParse(value, out var isbn) && isbn.Value == value,
        IdentifierType.Gtin13 => value.Length == 13
            && BarcodeText.IsAllDigits(value)
            && Gs1CheckDigit.IsValid(value)
            && !(Isbn13.IsBooklandPrefix(value)),
        IdentifierType.Gtin8 => value.Length == 8 && BarcodeText.IsAllDigits(value) && Gs1CheckDigit.IsValid(value),
        IdentifierType.Asin => value.Length == 10 && value.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)),
        _ => false,
    };
}
