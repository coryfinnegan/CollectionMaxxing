using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// One span of a read title: the words kept, and the words dropped from its ends to make it (for the trace). <paramref name="DroppedStart"/>
/// is the words dropped from its start alone, as read ("" when none were).
/// </summary>
public sealed record TrimmedSpan(string Text, string Dropped, string DroppedStart = "");

/// <summary>
/// The spans a spine read is asked again with when the whole read found nothing. Vision reads a shelf spine with an author's
/// first name, a publisher's mark or OCR junk at one end ("SARAH J. CROWN OF MIDNIGHT"), and the whole string then matches no
/// title, while the same words without the stray ones at their ends do. A span is the read's words with some dropped from its
/// ends, the rest kept in order. Pure; the retry itself is <see cref="IdentifyGrounding.RetryTrimmedAsync"/>.
/// </summary>
/// <remarks>
/// Not <c>TextSearchResolver</c>'s own ladder, which drops words to find more releases and still scores them against the whole
/// read: a release found that way is scored against the words that were read, so a stray author's name sinks it. A span is
/// scored as the read's title.
/// </remarks>
public static class TrimmedSpans
{
    /// <summary>A read of fewer words than this never retries: two words are all title (dropping one leaves a single word, which finds anything).</summary>
    public const int MinWordsInRead = 3;

    /// <summary>The most spans the providers are asked about for one read.</summary>
    public const int MaxSpans = 4;

    /// <summary>A span must keep at least this many words...</summary>
    public const int MinWordsKept = 2;

    /// <summary>...unless it is one word of at least this many letters.</summary>
    public const int MinLettersOfOneWord = 7;

    /// <summary>The words (start, end) dropped for each span, in the order the spans are tried.</summary>
    private static readonly (int Start, int End)[] Drops = [(1, 0), (0, 1), (1, 1), (2, 0), (0, 2)];

    /// <summary>
    /// The spans of <paramref name="title"/> to try, in order: one word off the start; one off the end; one off each end; two off
    /// the start; two off the end. A span that keeps too little to search for is left out (<see cref="MinWordsKept"/>,
    /// <see cref="MinLettersOfOneWord"/>), and so is one that is only an edition phrase (<see cref="EditionPhrases"/>) or has the
    /// same words (whatever their case) as a span already in the list.
    /// At most <see cref="MaxSpans"/>. None for a title of fewer than <see cref="MinWordsInRead"/> words, or no title.
    /// </summary>
    public static IReadOnlyList<TrimmedSpan> For(string? title)
    {
        var words = (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < MinWordsInRead)
        {
            return [];
        }

        var spans = new List<TrimmedSpan>(MaxSpans);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (start, end) in Drops)
        {
            var kept = words[start..(words.Length - end)];
            if (!WorthSearching(kept))
            {
                continue;
            }

            // An edition phrase is no title to search for (#815): "X DIRECTOR'S CUT" without its first word is "DIRECTOR'S CUT".
            var text = string.Join(' ', kept);
            if (EditionPhrases.IsOnlyAPhrase(text) || !tried.Add(text))
            {
                continue;
            }

            spans.Add(new TrimmedSpan(text, Dropped(words, start, end), string.Join(' ', words[..start])));
            if (spans.Count == MaxSpans)
            {
                break;
            }
        }

        return spans;
    }

    /// <summary>
    /// Whether a release a span found carries the read's leading words: the first distinctive word of the read, and every distinctive word
    /// the span dropped from its start, each a word of the release's title or of one of its creators' names. A distinctive word is a title
    /// word (<see cref="TitleWordSet"/>: two letters or more, no stop word) that is not only digits, so a leading "THE", an initial or a
    /// sticker number asks nothing. True when the read has no such word. Pure; <see cref="IdentifyGrounding"/> asks it for a read that is
    /// only a reader's title, with no text or lines of print (#815).
    /// </summary>
    public static bool FindCarriesLeadingWords(string? read, TrimmedSpan span, string? title, IEnumerable<string> creators)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(creators);

        var leading = (read ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Distinctive).FirstOrDefault(words => words.Count > 0) ?? [];
        var needed = Distinctive(span.DroppedStart);
        needed.UnionWith(leading);
        if (needed.Count == 0)
        {
            return true;
        }

        var carried = TitleWordSet.Of(title);
        foreach (var creator in creators)
        {
            carried.UnionWith(TitleWordSet.Of(creator));
        }

        return needed.All(carried.Contains);
    }

    private static HashSet<string> Distinctive(string? text) => [.. TitleWordSet.Of(text).Where(word => !word.All(char.IsAsciiDigit))];

    /// <summary>Whether words are enough to search for: at least <see cref="MinWordsKept"/>, or one of at least <see cref="MinLettersOfOneWord"/> letters.</summary>
    internal static bool WorthSearching(string[] kept) =>
        kept.Length >= MinWordsKept || (kept.Length == 1 && kept[0].Count(char.IsLetter) >= MinLettersOfOneWord);

    /// <summary>The dropped words in the order they were read; "start, end" when both ends lost some.</summary>
    private static string Dropped(string[] words, int start, int end)
    {
        var sides = new List<string>(2);
        if (start > 0)
        {
            sides.Add(string.Join(' ', words[..start]));
        }

        if (end > 0)
        {
            sides.Add(string.Join(' ', words[^end..]));
        }

        return string.Join(", ", sides);
    }
}
