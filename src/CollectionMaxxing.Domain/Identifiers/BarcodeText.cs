using System.Text.RegularExpressions;

namespace CollectionMaxxing.Domain.Identifiers;

/// <summary>
/// The text clean-up shared by every identifier parser: whitespace, hyphens, an "ISBN" label and EAN add-ons are
/// tolerated, so that what a scanner reads or a person types ends up as plain digits (docs/02 section 2).
/// </summary>
internal static partial class BarcodeText
{
    [GeneratedRegex(@"^isbn(?:[-\s]?1[03])?\s*:?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsbnLabel();

    /// <summary>
    /// Returns the code with the label, hyphens, spaces and any 2 or 5 digit add-on removed, or null when nothing is left.
    /// It does not check that the result is made of digits.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = IsbnLabel().Replace(raw.Trim(), string.Empty);
        text = RemoveHyphens(text);
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        // "9780306406157 12345": a space-separated add-on. Anything else with spaces is a code written in groups.
        if (tokens.Length == 2 && tokens[1].Length is 2 or 5 && IsAllDigits(tokens[1]) && tokens[0].Length is 8 or 10 or 12 or 13)
        {
            // "03064061 52" is an 8 digit code with a 2 digit add-on, but it is also the ISBN-10 0306406152 written in
            // two groups. A valid ISBN-10 is far less likely to be a coincidence than an add-on, so the ISBN-10 wins.
            var whole = tokens[0] + tokens[1];
            return tokens[0].Length == 8 && whole.Length == 10 && Isbn10.IsValidDigits(whole) ? whole : tokens[0];
        }

        var joined = string.Concat(tokens);

        // "978030640615712345": a run-on add-on after a 12 or 13 digit code. 8 digit codes are never trimmed, because
        // 8 + 2 collides with an ISBN-10 and 8 + 5 with an EAN-13. 12 + 2 is never trimmed either: 14 digits is a
        // GTIN-14, and cutting it to 12 would turn one product's code into another's (see Gtin.TryParse).
        if (IsAllDigits(joined))
        {
            return joined.Length switch
            {
                17 => joined[..12],
                15 or 18 => joined[..13],
                _ => joined,
            };
        }

        return joined;
    }

    public static bool IsAllDigits(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string RemoveHyphens(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            // Hyphen-minus, hyphen, non-breaking hyphen, figure/en/em dashes, minus sign.
            if (c is '-' or '‐' or '‑' or '‒' or '–' or '—' or '−')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
