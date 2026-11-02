namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>One span of a read title: the words kept, and the words dropped from its ends to make it (for the trace).</summary>
public sealed record TrimmedSpan(string Text, string Dropped);

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
    /// <see cref="MinLettersOfOneWord"/>), and so is one with the same words (whatever their case) as a span already in the list.
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

            var text = string.Join(' ', kept);
            if (!tried.Add(text))
            {
                continue;
            }

            spans.Add(new TrimmedSpan(text, Dropped(words, start, end)));
            if (spans.Count == MaxSpans)
            {
                break;
            }
        }

        return spans;
    }

    private static bool WorthSearching(string[] kept) =>
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
