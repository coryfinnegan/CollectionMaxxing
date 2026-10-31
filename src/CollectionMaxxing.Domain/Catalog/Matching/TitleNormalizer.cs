using System.Text;

namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// Title comparison for matching (docs/02 section 6): case, accents, punctuation, a leading or trailing article, roman
/// numerals and "&amp;" versus "and" do not matter, and a small misread is forgiven.
/// </summary>
public static class TitleNormalizer
{
    private static readonly (char To, string From)[] Accents =
    [
        ('a', "àáâãäåāăą"), ('c', "çćč"), ('d', "ďđ"), ('e', "èéêëēĕėęě"), ('g', "ğ"), ('i', "ìíîïĩīĭįı"),
        ('l', "ł"), ('n', "ñńň"), ('o', "òóôõöøōŏő"), ('r', "ŕř"), ('s', "śšş"), ('t', "ť"),
        ('u', "ùúûüũūŭůűų"), ('y', "ýÿ"), ('z', "źżž"),
    ];

    private static readonly Dictionary<char, string> AccentMap = BuildAccentMap();

    // Folding turns one character into at most this many ("&" becomes " and "), so a buffer of this many per character holds the fold.
    private const int MostFoldedPerChar = 5;

    // A title this short is worked on the stack; a longer one gets a buffer of its own.
    private const int MostStackedTitleLength = 128;

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

        var capacity = title.Length * MostFoldedPerChar;
        Span<char> buffer = title.Length <= MostStackedTitleLength ? stackalloc char[capacity] : new char[capacity];
        var folded = buffer[..Fold(title, buffer)];

        // "Matrix, The" is "The Matrix", and a leading article is dropped from a title of more than one word, so the
        // article of "Matrix, The" is dropped with it: the key is the words before the comma, unless there are none.
        var trailing = TrailingArticle(folded, out var article, out var beforeComma);
        var length = Compact(trailing ? beforeComma : folded, buffer);
        if (trailing)
        {
            return length > 0 ? new string(buffer[..length]) : new string(article);
        }

        var words = buffer[..length];
        var firstEnd = words.IndexOf(' ');
        if (firstEnd > 0 && words[..firstEnd] is "the" or "a" or "an")
        {
            words = words[(firstEnd + 1)..];
        }

        return new string(words);
    }

    /// <summary>
    /// A title with its accents taken off and nothing else changed (case, spacing and punctuation stay): "TÉENAGE" gives
    /// "TEENAGE", "Œuvre" gives "Oeuvre". For a search query, where an accent an OCR read puts on an English word is noise.
    /// Null gives "".
    /// </summary>
    public static string StripAccents(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var raw in text.Normalize(NormalizationForm.FormC))
        {
            if (!AccentMap.TryGetValue(char.ToLowerInvariant(raw), out var plain))
            {
                builder.Append(raw);
            }
            else if (char.IsUpper(raw))
            {
                builder.Append(char.ToUpperInvariant(plain[0])).Append(plain, 1, plain.Length - 1);
            }
            else
            {
                builder.Append(plain);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// What a title that is only the other one's main title is worth: "Kitchen Confidential" read off a cover against
    /// "Kitchen Confidential: Adventures in the Culinary Underbelly" from a provider, or the other way round. Close, never
    /// the same: a missing subtitle can tell two releases apart ("Metroid Prime 2" is pinned Ambiguous against "Metroid
    /// Prime 2: Echoes"), so it stays under 0.845.
    /// </summary>
    public const double MainTitleCredit = 0.8;

    /// <summary>
    /// What the same words in another order are worth as a title: "kitchen orchard" against "The Orchard Kitchen". Close,
    /// never the same. Word order is the one thing that tells these titles apart, and a reader that misorders lines or
    /// words (a spine read bottom to top) has not read the title. A match that rests on this alone must not be added
    /// automatically.
    /// </summary>
    public const double ReorderedWordsCredit = 0.9;

    /// <summary>
    /// How alike two titles are, from 0 (nothing in common) to 1 (the same after normalising). Takes the better of an
    /// edit-distance ratio (forgives misreads and plurals) and a word-overlap ratio (forgives word order). A sequel
    /// number that only one side has costs a quarter of the score, so "Metroid Prime" does not match "Metroid Prime 2".
    /// A trailing year ("Alien 1979") is ignored if that gives a better match, unless both titles carry a year and the
    /// years differ: then the year is part of each title's identity ("Madden NFL 2005" is not "Madden NFL 2004") and is
    /// compared like any other word. A title compared with the other's main title (the part before a colon, a spaced dash
    /// or a bracket) earns <see cref="MainTitleCredit"/> of that comparison when it is the better one; two main titles are
    /// never compared with each other, so two subtitles of one series ("Zelda: Ocarina of Time", "Zelda: Majora's Mask")
    /// stay apart. The same words in any order are the same here, which suits names ("Odell, Mara" is "Mara Odell");
    /// <see cref="TitleSimilarity"/> is the stricter comparison for titles. To compare one title with many, make each a
    /// <see cref="ComparableTitle"/> once.
    /// </summary>
    public static double Similarity(string? a, string? b) => new ComparableTitle(a).Similarity(new ComparableTitle(b));

    /// <summary>
    /// <see cref="Similarity"/> for two titles: the same words in another order score <see cref="ReorderedWordsCredit"/>
    /// at most, so only a title that is the same once the article, case, accents, punctuation, numerals and "&amp;" are
    /// set aside (and a year on one side only) scores 1.
    /// </summary>
    public static double TitleSimilarity(string? a, string? b) => new ComparableTitle(a).TitleSimilarity(new ComparableTitle(b));

    // Lower case, accents folded, "&" as "and", apostrophes and periods dropped (so "Player's" is "players" and "Bros." is
    // "bros"), every other symbol a space. Returns how much of the destination it filled.
    private static int Fold(string title, Span<char> destination)
    {
        var length = 0;
        foreach (var raw in title)
        {
            var c = char.ToLowerInvariant(raw);

            // Every accented letter is outside ASCII, so an ASCII character needs no lookup.
            if (c >= 0x80 && AccentMap.TryGetValue(c, out var plain))
            {
                plain.CopyTo(destination[length..]);
                length += plain.Length;
            }
            else if (c == '&')
            {
                " and ".CopyTo(destination[length..]);
                length += 5;
            }
            else if (c is '\'' or '’' or '‘' or '`' or '.')
            {
                continue;
            }
            else if (char.IsLetterOrDigit(c) || c == ',')
            {
                destination[length++] = c;
            }
            else
            {
                destination[length++] = ' ';
            }
        }

        return length;
    }

    // "matrix, the": a comma, then one of the three articles, then nothing but spaces (the fold leaves no other white
    // space). The comma is the one before the article, so what comes before it is everything else.
    private static bool TrailingArticle(ReadOnlySpan<char> folded, out ReadOnlySpan<char> article, out ReadOnlySpan<char> beforeComma)
    {
        var end = folded.TrimEnd(' ').Length;
        foreach (var candidate in (ReadOnlySpan<string>)["the", "an", "a"])
        {
            if (!folded[..end].EndsWith(candidate, StringComparison.Ordinal))
            {
                continue;
            }

            var before = folded[..(end - candidate.Length)].TrimEnd(' ');
            if (before.Length > 0 && before[^1] == ',')
            {
                article = folded[(end - candidate.Length)..end];
                beforeComma = before[..^1];
                return true;
            }
        }

        article = default;
        beforeComma = default;
        return false;
    }

    // The words of the text (it is split at spaces and commas), with roman numerals as digits, joined by single spaces
    // and written to the front of the destination, which may be the buffer the text lies in: a word is never written
    // past where it was read from. Returns the length written.
    private static int Compact(ReadOnlySpan<char> text, Span<char> destination)
    {
        var length = 0;
        var at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && text[at] is ' ' or ',')
            {
                at++;
            }

            var start = at;
            while (at < text.Length && text[at] is not (' ' or ','))
            {
                at++;
            }

            if (at == start)
            {
                break;
            }

            if (length > 0)
            {
                destination[length++] = ' ';
            }

            var word = text[start..at];
            var written = Numeral(word) is { } digits ? digits : word;
            written.CopyTo(destination[length..]);
            length += written.Length;
        }

        return length;
    }

    // Roman numerals up to twenty, written with two or more letters. A single I, V or X is left alone on purpose: it is
    // far more often a letter ("Mega Man X", "V for Vendetta", "Malcolm X") than a number, and turning "Mega Man X"
    // into "Mega Man 10" would match the wrong game.
    private static string? Numeral(ReadOnlySpan<char> word) => word switch
    {
        "ii" => "2",
        "iii" => "3",
        "iv" => "4",
        "vi" => "6",
        "vii" => "7",
        "viii" => "8",
        "ix" => "9",
        "xi" => "11",
        "xii" => "12",
        "xiii" => "13",
        "xiv" => "14",
        "xv" => "15",
        "xvi" => "16",
        "xvii" => "17",
        "xviii" => "18",
        "xix" => "19",
        "xx" => "20",
        _ => null,
    };

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
