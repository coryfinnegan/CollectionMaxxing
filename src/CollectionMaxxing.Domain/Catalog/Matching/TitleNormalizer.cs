using System.Text;
using System.Text.RegularExpressions;

namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// Title comparison for matching (docs/02 section 6): case, accents, punctuation, a leading or trailing article, roman
/// numerals and "&amp;" versus "and" do not matter, and a small misread is forgiven.
/// </summary>
public static partial class TitleNormalizer
{
    // Roman numerals up to twenty, written with two or more letters. A single I, V or X is left alone on purpose: it is
    // far more often a letter ("Mega Man X", "V for Vendetta", "Malcolm X") than a number, and turning "Mega Man X"
    // into "Mega Man 10" would match the wrong game.
    private static readonly Dictionary<string, string> RomanNumerals = new(StringComparer.Ordinal)
    {
        ["ii"] = "2",
        ["iii"] = "3",
        ["iv"] = "4",
        ["vi"] = "6",
        ["vii"] = "7",
        ["viii"] = "8",
        ["ix"] = "9",
        ["xi"] = "11",
        ["xii"] = "12",
        ["xiii"] = "13",
        ["xiv"] = "14",
        ["xv"] = "15",
        ["xvi"] = "16",
        ["xvii"] = "17",
        ["xviii"] = "18",
        ["xix"] = "19",
        ["xx"] = "20",
    };

    private static readonly (char To, string From)[] Accents =
    [
        ('a', "àáâãäåāăą"), ('c', "çćč"), ('d', "ďđ"), ('e', "èéêëēĕėęě"), ('g', "ğ"), ('i', "ìíîïĩīĭįı"),
        ('l', "ł"), ('n', "ñńň"), ('o', "òóôõöøōŏő"), ('r', "ŕř"), ('s', "śšş"), ('t', "ť"),
        ('u', "ùúûüũūŭůűų"), ('y', "ýÿ"), ('z', "źżž"),
    ];

    private static readonly Dictionary<char, string> AccentMap = BuildAccentMap();

    [GeneratedRegex(@"^(.*?),\s*(the|a|an)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingArticle();

    /// <summary>
    /// Reduces a title to a comparison key. "The Matrix" and "Matrix, The" give the same key, as do "Final Fantasy VII" and
    /// "Final Fantasy 7", and "Ratchet &amp; Clank" and "Ratchet and Clank". Null, blank or punctuation-only gives "".
    /// </summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var text = Fold(title);
        var trailing = TrailingArticle().Match(text);
        if (trailing.Success)
        {
            text = trailing.Groups[2].Value + " " + trailing.Groups[1].Value;
        }

        var tokens = Tokenize(text);
        if (tokens.Count > 1 && tokens[0] is "the" or "a" or "an")
        {
            tokens.RemoveAt(0);
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (RomanNumerals.TryGetValue(tokens[i], out var digits))
            {
                tokens[i] = digits;
            }
        }

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// What a title that is only the other one's main title is worth: "Kitchen Confidential" read off a cover against
    /// "Kitchen Confidential: Adventures in the Culinary Underbelly" from a provider, or the other way round. Close, never
    /// the same: a missing subtitle can tell two releases apart ("Metroid Prime 2" is pinned Ambiguous against "Metroid
    /// Prime 2: Echoes"), so it stays under 0.845.
    /// </summary>
    public const double MainTitleCredit = 0.8;

    /// <summary>
    /// How alike two titles are, from 0 (nothing in common) to 1 (the same after normalising). Takes the better of an
    /// edit-distance ratio (forgives misreads and plurals) and a word-overlap ratio (forgives word order). A sequel
    /// number that only one side has costs a quarter of the score, so "Metroid Prime" does not match "Metroid Prime 2".
    /// A trailing year ("Alien 1979") is ignored if that gives a better match, unless both titles carry a year and the
    /// years differ: then the year is part of each title's identity ("Madden NFL 2005" is not "Madden NFL 2004") and is
    /// compared like any other word. A title compared with the other's main title (the part before a colon, a spaced dash
    /// or a bracket) earns <see cref="MainTitleCredit"/> of that comparison when it is the better one; two main titles are
    /// never compared with each other, so two subtitles of one series ("Zelda: Ocarina of Time", "Zelda: Majora's Mask")
    /// stay apart.
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        var whole = WholeSimilarity(a, b);
        if (whole >= 1.0)
        {
            return whole;
        }

        var best = whole;
        if (MainTitle(a) is { } mainA)
        {
            best = Math.Max(best, MainTitleCredit * WholeSimilarity(mainA, b));
        }

        if (MainTitle(b) is { } mainB)
        {
            best = Math.Max(best, MainTitleCredit * WholeSimilarity(a, mainB));
        }

        return best;
    }

    /// <summary>The part before a subtitle, or null when the title has none (or nothing would be left of it).</summary>
    private static string? MainTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var cut = title.Length;
        foreach (var marker in SubtitleMarkers)
        {
            var at = title.IndexOf(marker, StringComparison.Ordinal);
            if (at > 0 && at < cut)
            {
                cut = at;
            }
        }

        if (cut == title.Length)
        {
            return null;
        }

        var main = title[..cut];
        return Normalize(main).Length == 0 ? null : main;
    }

    private static readonly string[] SubtitleMarkers = [":", " - ", " – ", " — ", "("];

    private static double WholeSimilarity(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
        {
            return 0.0;
        }

        if (YearsConflict(left, right))
        {
            return Core(left, right);
        }

        var leftNoYear = WithoutTrailingYear(left);
        var rightNoYear = WithoutTrailingYear(right);
        var best = Core(left, right);
        if (leftNoYear != left)
        {
            best = Math.Max(best, Core(leftNoYear, right));
        }

        if (rightNoYear != right)
        {
            best = Math.Max(best, Core(left, rightNoYear));
        }

        if (leftNoYear != left && rightNoYear != right)
        {
            best = Math.Max(best, Core(leftNoYear, rightNoYear));
        }

        return best;
    }

    /// <summary>
    /// True when both titles contain a four digit year (1900 to 2099) and the years are not the same: an annual series
    /// ("Madden NFL 2005" against "Madden NFL 2004"). A year that only one title has is not a conflict.
    /// </summary>
    internal static bool TitleYearsConflict(string? a, string? b) => YearsConflict(Normalize(a), Normalize(b));

    private static bool YearsConflict(string normalizedLeft, string normalizedRight)
    {
        var left = YearsIn(normalizedLeft);
        var right = YearsIn(normalizedRight);
        return left.Count > 0 && right.Count > 0 && !left.SetEquals(right);
    }

    private static HashSet<int> YearsIn(string normalized)
    {
        var years = new HashSet<int>();
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsYear(token, out var year))
            {
                years.Add(year);
            }
        }

        return years;
    }

    private static string WithoutTrailingYear(string normalized)
    {
        var space = normalized.LastIndexOf(' ');
        return space > 0 && IsYear(normalized.AsSpan(space + 1), out _) ? normalized[..space] : normalized;
    }

    private static bool IsYear(ReadOnlySpan<char> token, out int year)
    {
        year = 0;
        if (token.Length != 4)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        year = ((token[0] - '0') * 1000) + ((token[1] - '0') * 100) + ((token[2] - '0') * 10) + (token[3] - '0');
        return year is >= 1900 and <= 2099;
    }

    private static double Core(string left, string right)
    {
        if (left == right)
        {
            return 1.0;
        }

        var edit = 1.0 - ((double)EditDistance(left, right) / Math.Max(left.Length, right.Length));

        var leftTokens = left.Split(' ');
        var rightTokens = right.Split(' ');
        var leftSet = new HashSet<string>(leftTokens, StringComparer.Ordinal);
        var rightSet = new HashSet<string>(rightTokens, StringComparer.Ordinal);
        var shared = leftSet.Count(rightSet.Contains);
        var union = leftSet.Count + rightSet.Count - shared;
        var overlap = union == 0 ? 0.0 : (double)shared / union;

        var score = Math.Max(edit, overlap);

        if (!NumberTokens(leftSet).SetEquals(NumberTokens(rightSet)))
        {
            score *= 0.75;
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    private static HashSet<string> NumberTokens(HashSet<string> tokens) =>
        [.. tokens.Where(t => t.All(char.IsAsciiDigit))];

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    // Lower case, accents folded, "&" as "and", apostrophes and periods dropped (so "Player's" is "players" and "Bros." is
    // "bros"), every other symbol a space.
    private static string Fold(string title)
    {
        var builder = new StringBuilder(title.Length + 8);
        foreach (var raw in title)
        {
            var c = char.ToLowerInvariant(raw);
            if (AccentMap.TryGetValue(c, out var plain))
            {
                builder.Append(plain);
            }
            else if (c == '&')
            {
                builder.Append(" and ");
            }
            else if (c is '\'' or '’' or '‘' or '`' or '.')
            {
                continue;
            }
            else if (char.IsLetterOrDigit(c) || c == ',')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }

    private static List<string> Tokenize(string folded) =>
        [.. folded.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    private static Dictionary<char, string> BuildAccentMap()
    {
        var map = new Dictionary<char, string>();
        foreach (var (to, source) in Accents)
        {
            foreach (var c in source)
            {
                map[c] = to.ToString();
            }
        }

        map['ß'] = "ss";
        map['æ'] = "ae";
        map['œ'] = "oe";
        return map;
    }
}
