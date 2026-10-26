using System.Text;

namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// Headline casing for display (#353): provider titles arrive sentence-cased ("So you want to be a wizard") or all-caps
/// and would look sloppy next to well-cased ones, so every read path shows <see cref="ToTitleCase"/> of the stored title.
/// Stored titles stay verbatim, and a person's CustomTitle never passes through here.
/// </summary>
/// <remarks>
/// Chicago-ish: principal words are capitalised, small words stay lowercase mid-title, and the first word, the last word
/// and the first word after a colon are always capitalised. Untouched: words with internal capitals (iPhone, McDonald,
/// NASA, D&amp;D), roman numerals (upper-cased: "final fantasy ix" becomes "Final Fantasy IX"), numbers, hyphenated parts
/// (each part is cased: "Spider-Man"), apostrophes ("O'Brien", possessive "Player's"), and Mc- names ("mcdonald" becomes
/// "McDonald"). A title that is already correctly cased comes out byte-identical.
/// <para/>
/// Known simplifications, documented rather than guessed at: verb particles stay lowercase mid-title ("Stand up for...");
/// every hyphen part is capitalised ("Mother-In-Law"); bare "Mac" is left to internal-capital preservation ("machine"
/// rules out a general Mac- rule); an acronym in an all-caps title cannot be told from a shouted word, so only
/// &amp;-initialisms ("D&amp;D") and dotted ones ("U.S.A.") survive that path; and a deliberately lowercase title
/// ("in rainbows") is cased like any other, because stored provider data is never intentionally stylised — the person's
/// CustomTitle is the escape hatch for all of these.
/// </remarks>
public static class TitleCaser
{
    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "but", "or", "nor", "for", "so", "yet", "as", "at", "by", "in", "of", "off", "on",
        "per", "to", "up", "via", "with", "from", "into", "over", "after", "before", "between", "through", "during",
        "under", "around", "among", "without", "within", "along", "across", "behind", "beyond", "except", "until",
        "than", "versus", "vs", "n",
    };

    private static readonly HashSet<char> Apostrophes = ['\'', '’', '‘', '`'];

    private static readonly HashSet<char> Hyphens = ['-', '‐', '‑'];

    /// <summary>Cases one title for display. Null or blank gives ""; anything else keeps its spacing and punctuation.</summary>
    public static string ToTitleCase(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // An all-caps title is a shouted title, not a title of acronyms: lower it first so the rules below apply.
        var hasLower = false;
        var hasUpper = false;
        foreach (var c in title)
        {
            if (char.IsLower(c))
            {
                hasLower = true;
            }
            else if (char.IsUpper(c))
            {
                hasUpper = true;
            }
        }

        var tokens = Tokenize(hasUpper && !hasLower ? title.ToLowerInvariant() : title);
        var first = -1;
        var last = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].IsWord && Core(tokens[i].Text).Length > 0)
            {
                first = first == -1 ? i : first;
                last = i;
            }
        }

        var builder = new StringBuilder(title.Length);
        var afterColon = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i].Text;
            if (!tokens[i].IsWord)
            {
                builder.Append(token);
                continue;
            }

            var core = Core(token);
            builder.Append(core.Length == 0 ? token : Case(token, core, i == first, i == last, afterColon));
            afterColon = token.EndsWith(':');
        }

        return builder.ToString();
    }

    private sealed record Token(string Text, bool IsWord);

    // Words are runs between whitespace and en/em dashes; separators are kept verbatim so spacing survives.
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        var currentIsWord = false;
        var started = false;
        foreach (var c in text)
        {
            var isWord = !char.IsWhiteSpace(c) && c is not '–' and not '—';
            if (!started || isWord != currentIsWord)
            {
                if (started)
                {
                    tokens.Add(new Token(current.ToString(), currentIsWord));
                    current.Clear();
                }

                currentIsWord = isWord;
                started = true;
            }

            current.Append(c);
        }

        if (started)
        {
            tokens.Add(new Token(current.ToString(), currentIsWord));
        }

        return tokens;
    }

    // The word without its surrounding punctuation ("'n'" gives "n", "rings:" gives "rings", "&" gives "").
    private static string Core(string word)
    {
        var start = 0;
        var end = word.Length;
        while (start < end && !char.IsLetterOrDigit(word[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetterOrDigit(word[end - 1]))
        {
            end--;
        }

        return word[start..end];
    }

    private static string Case(string word, string core, bool first, bool last, bool afterColon)
    {
        var cased = IsRomanNumeral(core)
            ? core.ToUpperInvariant()
            : SmallWords.Contains(core.ToLowerInvariant()) && !first && !last && !afterColon
                && (core.Length > 1 || char.IsLower(core[0]))
                ? core.ToLowerInvariant()
                : HasInternalCapitals(core)
                    ? core
                    : Capitalize(core, first || last || afterColon);
        return cased == core ? word : word.Replace(core, cased, StringComparison.Ordinal);
    }

    private static bool HasInternalCapitals(string core)
    {
        for (var i = 1; i < core.Length; i++)
        {
            if (char.IsUpper(core[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string Capitalize(string core, bool force)
    {
        if (core.Length == 1)
        {
            // A lone capital ("Mega Man X", a grade "A") is kept; a lone small letter follows the small-word rule.
            return char.IsUpper(core[0]) || (!force && SmallWords.Contains(core.ToLowerInvariant()))
                ? core
                : core.ToUpperInvariant();
        }

        if (core.Equals("mc", StringComparison.OrdinalIgnoreCase))
        {
            return "MC"; // bare "mc" is an emcee (MC Hammer), never a name fragment
        }

        if (core.Contains('.') && IsDottedInitialism(core))
        {
            return core.ToUpperInvariant();
        }

        if (core.Contains('&'))
        {
            return CaseAmpersandWord(core);
        }

        var parts = SplitKeeping(core, Hyphens.Contains);
        var builder = new StringBuilder(core.Length);
        for (var i = 0; i < parts.Count; i++)
        {
            builder.Append(i % 2 == 1 ? parts[i] : CaseApostrophes(parts[i], i == 0));
        }

        return builder.ToString();
    }

    // "o'brien" becomes "O'Brien" but possessive "player's" keeps its small s; "dogs'" never reaches here (its
    // apostrophe is surrounding punctuation). The Mc- rule only fits the start of the word ("mcdonald's" is fine).
    private static string CaseApostrophes(string part, bool wordStart)
    {
        var segments = SplitKeeping(part, Apostrophes.Contains);
        var builder = new StringBuilder(part.Length);
        for (var i = 0; i < segments.Count; i++)
        {
            if (i % 2 == 1)
            {
                builder.Append(segments[i]);
            }
            else if (segments[i].Length == 0 || (i > 0 && string.Equals(segments[i], "s", StringComparison.OrdinalIgnoreCase)))
            {
                builder.Append(segments[i].ToLowerInvariant());
            }
            else
            {
                builder.Append(UpperFirst(segments[i].ToLowerInvariant()));
            }
        }

        var cased = builder.ToString();
        return wordStart && cased.StartsWith("Mc", StringComparison.Ordinal) && cased.Length > 2 && char.IsLower(cased[2])
            ? "Mc" + char.ToUpperInvariant(cased[2]) + cased[3..]
            : cased;
    }

    // "d&d", "r&b" and "at&t" are initialisms and stay shouted; anything longer is words that lost their spaces.
    private static string CaseAmpersandWord(string core)
    {
        var segments = core.Split('&');
        if (segments.All(s => s.Length is >= 1 and <= 2 && s.All(char.IsLetter)))
        {
            return core.ToUpperInvariant();
        }

        return string.Join('&', segments.Select(s => s.Length == 0 ? s : UpperFirst(s.ToLowerInvariant())));
    }

    private static bool IsDottedInitialism(string core)
    {
        var letters = 0;
        foreach (var c in core)
        {
            if (c == '.')
            {
                if (letters == 0)
                {
                    return false;
                }

                letters = 0;
            }
            else if (char.IsLetter(c) && letters == 0)
            {
                letters++;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    // A roman numeral proper: two or more numeral letters, a value in sequel range, and canonical ("mix" is 1009 and
    // "civil" is not canonical, so neither counts; single I, V and X are letters first, as in TitleNormalizer).
    private static bool IsRomanNumeral(string core)
    {
        if (core.Length < 2 || core.Any(c => !"ivxlcdmIVXLCDM".Contains(c)))
        {
            return false;
        }

        var upper = core.ToUpperInvariant();
        var value = RomanValue(upper);
        return value is >= 1 and <= 100 && ToRoman(value) == upper;
    }

    private static int RomanValue(string numeral)
    {
        var total = 0;
        for (var i = 0; i < numeral.Length; i++)
        {
            var current = RomanDigit(numeral[i]);
            total += i + 1 < numeral.Length && RomanDigit(numeral[i + 1]) > current ? -current : current;
        }

        return total;
    }

    private static int RomanDigit(char c) => c switch
    {
        'I' => 1,
        'V' => 5,
        'X' => 10,
        'L' => 50,
        'C' => 100,
        'D' => 500,
        'M' => 1000,
        _ => 0,
    };

    private static string ToRoman(int value)
    {
        var builder = new StringBuilder();
        foreach (var (digits, numeral) in new[] { (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
        {
            while (value >= digits)
            {
                builder.Append(numeral);
                value -= digits;
            }
        }

        return builder.ToString();
    }

    private static string UpperFirst(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // Splits on separator chars, keeping them as every odd entry, so rejoining is exact.
    private static List<string> SplitKeeping(string text, Func<char, bool> isSeparator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (isSeparator(c))
            {
                parts.Add(current.ToString());
                parts.Add(c.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString());
        return parts;
    }
}
