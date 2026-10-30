using System.Text;

namespace CollectionMaxxing.Application.GameSerials;

/// <summary>
/// A game's product code as printed on its cartridge label or disc (<c>SNS-ADNE-USA</c>, <c>NUS-NSME-USA</c>, <c>SLUS-00594</c>),
/// in one spelling. The phone reads it as text, so it arrives in upper or lower case, with spaces, underscores or dashes where the
/// print has a hyphen, and with a dot inside a PlayStation disc's digits (<c>SLUS_005.94</c>). The bundled serial table is spelled
/// the same way, so a code is found by comparing normal forms. Pure.
/// </summary>
/// <remarks>
/// <b>A code is</b> a run of up to six letters, then one to five more groups of letters and digits joined by hyphens
/// (<c>SNS-8C</c>, <c>DL-DOL-GALE-USA</c>), five to <see cref="MaxLength"/> characters long. Anything else is not a code:
/// a bare number, a word, or text with punctuation a label never prints. A code the ocr ran together
/// (<c>SLUS00594</c>) gets its hyphen back between the letters and the digits.
/// </remarks>
public static class ProductCode
{
    /// <summary>The longest a code may be as the phone sends it, spaces and all.</summary>
    public const int MaxInputLength = 32;

    /// <summary>The longest a code may be once normalised.</summary>
    public const int MaxLength = 24;

    private const int MinLength = 5;
    private const int MaxLeadingLetters = 6;
    private const int MaxGroupLength = 8;
    private const int MaxGroups = 6;

    /// <summary>
    /// What a label or a list can add to the end of a code without changing which game it is: the region the print was made for
    /// (<c>SNS-8C-USA</c>), a language or version letter and a disc index (<c>SLES-02375-P</c>, <c>SLUS-00594-0</c>). The lookup
    /// tries a code as read, without one of these, then with each.
    /// </summary>
    public static readonly IReadOnlyList<string> Suffixes =
    [
        "USA", "EUR", "JPN", "NOE", "FRA", "ESP", "ITA", "UKV", "AUS", "CAN", "GER", "HOL", "KOR", "SCN", "FAH", "ANZ", "RUS", "SWE",
        "EUU", "EUT", "NOR", "BRA", "CHN", "ASI", "P", "T", "F", "0", "1", "2", "3", "4",
    ];

    private static readonly HashSet<string> SuffixSet = new(Suffixes, StringComparer.Ordinal);

    /// <summary>
    /// Whether a request may carry this code: at most <see cref="MaxInputLength"/> characters as sent, and the shape of a code
    /// once cleaned.
    /// </summary>
    public static bool IsAcceptable(string? raw) => raw is { Length: <= MaxInputLength } && Normalize(raw) is not null;

    /// <summary>The normal form of a code, or null when the text is not shaped like one.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxInputLength * 2)
        {
            return null;
        }

        var builder = new StringBuilder(raw.Length);
        var gap = false;
        var dot = false;
        foreach (var c in raw.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                // A lone dot between two digits is part of the number (SLUS_005.94), not a break between groups.
                if (gap && builder.Length > 0 && !(dot && char.IsAsciiDigit(builder[^1]) && char.IsAsciiDigit(c)))
                {
                    builder.Append('-');
                }

                gap = false;
                dot = false;
                builder.Append(char.ToUpperInvariant(c));
            }
            else if (IsSeparator(c))
            {
                dot = c == '.' && !gap;
                gap = true;
            }
            else
            {
                return null;
            }
        }

        var code = builder.ToString();
        if (code.Length == 0)
        {
            return null;
        }

        if (!code.Contains('-'))
        {
            code = SplitRunTogether(code);
        }

        return IsShapedLikeACode(code) ? code : null;
    }

    /// <summary>
    /// The code without its last group when that group is one of the <see cref="Suffixes"/> and a code is left
    /// (<c>SNS-8C-USA</c> gives <c>SNS-8C</c>); null when there is nothing to take off. Takes a normal form.
    /// </summary>
    public static string? WithoutSuffix(string code)
    {
        var cut = code.LastIndexOf('-');
        if (cut <= 0 || !SuffixSet.Contains(code[(cut + 1)..]))
        {
            return null;
        }

        var stem = code[..cut];
        return stem.Contains('-') ? stem : null;
    }

    private static bool IsSeparator(char c) => c is ' ' or '\t' or '-' or '_' or '.' or '/' or '‐' or '‑' or '‒' or '–' or '—';

    // "SLUS00594": letters, then a digit. A word, or letters only, has no digit to split at and stays one group (and is refused).
    private static string SplitRunTogether(string code)
    {
        var letters = 0;
        while (letters < code.Length && char.IsAsciiLetter(code[letters]))
        {
            letters++;
        }

        return letters is >= 2 and <= 5 && letters < code.Length && char.IsAsciiDigit(code[letters])
            ? string.Concat(code.AsSpan(0, letters), "-", code.AsSpan(letters))
            : code;
    }

    private static bool IsShapedLikeACode(string code)
    {
        if (code.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        var groups = code.Split('-');
        if (groups.Length is < 2 or > MaxGroups)
        {
            return false;
        }

        var first = groups[0];
        if (first.Length is 0 or > MaxLeadingLetters || !first.All(char.IsAsciiLetter))
        {
            return false;
        }

        return groups.Skip(1).All(g => g.Length is >= 1 and <= MaxGroupLength);
    }
}
