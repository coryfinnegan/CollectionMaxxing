using System.Diagnostics.CodeAnalysis;

namespace CollectionMaxxing.Domain.Identifiers;

/// <summary>A validated ISBN-10: nine digits and a mod-11 check character, which is <c>X</c> for ten.</summary>
public sealed record Isbn10
{
    private Isbn10(string value) => Value = value;

    /// <summary>Ten characters; a check digit of ten is always an upper-case <c>X</c>.</summary>
    public string Value { get; }

    /// <summary>Reads a typed ISBN-10. An "ISBN" label, spaces and hyphens are tolerated. Never throws.</summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out Isbn10? isbn)
    {
        isbn = null;
        var text = BarcodeText.Clean(input);
        if (text is null || text.Length != 10)
        {
            return false;
        }

        if (!IsValidDigits(text))
        {
            return false;
        }

        isbn = new Isbn10(text.ToUpperInvariant());
        return true;
    }

    /// <summary>True when the ten characters are nine digits and a correct mod-11 check character (<c>X</c> or <c>x</c> for ten).</summary>
    internal static bool IsValidDigits(string text)
    {
        if (text.Length != 10)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var c = text[i];
            int digit;
            if (char.IsAsciiDigit(c))
            {
                digit = c - '0';
            }
            else if (i == 9 && c is 'X' or 'x')
            {
                digit = 10;
            }
            else
            {
                return false;
            }

            sum += digit * (10 - i);
        }

        return sum % 11 == 0;
    }

    /// <summary>Prefixes 978 and recomputes the check digit.</summary>
    public Isbn13 ToIsbn13()
    {
        var body = "978" + Value[..9];
        return Isbn13.FromValidated(body + Gs1CheckDigit.For(body));
    }

    public override string ToString() => Value;
}
