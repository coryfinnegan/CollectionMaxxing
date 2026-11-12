using System.Text.RegularExpressions;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>A read title split at the edition phrase that ends it: the title without it, and the phrase as it was read.</summary>
public sealed record EditionSplit(string Title, string Phrase);

/// <summary>
/// The edition phrases a case or a cover prints after its title ("HIGHLANDER: DIRECTOR'S CUT", "... 25th Anniversary Edition",
/// "... Widescreen"). They say which release of a title this is, never which title, and a search that keeps them finds the wrong
/// work: "HIGHLANDER: DIRECTOR'S CUT" was asked as a possessive name ("HIGHLANDER: DIRECTOR'S") and the word left, "CUT", found a
/// film called "Cut!" (#815). Pure. The phrases are matched as whole words, apostrophes dropped and case aside, so "Director’s Cut"
/// and "DIRECTORS CUT" are one phrase; an ordinal in front of a phrase ("25th") belongs to it, a bare number does not.
/// </summary>
public static partial class EditionPhrases
{
    /// <summary>The phrases, lower case, apostrophes dropped, one space between words. Also banner phrases (<see cref="FragmentReads"/>).</summary>
    internal static readonly string[] Phrases =
    [
        "widescreen", "full screen", "fullscreen", "directors cut", "special edition", "anniversary edition", "collectors edition",
        "special collectors edition",
    ];

    private static readonly string[][] PhraseWords = [.. Phrases.Select(p => p.Split(' ')).OrderByDescending(p => p.Length)];

    /// <summary>What a title's last kept word loses at its end once the phrase after it is gone: the colon, dash or bracket that set the phrase off.</summary>
    private static readonly char[] Separators = [':', ';', ',', '-', '–', '—', '(', '[', '/', '|'];

    private static readonly char[] Brackets = ['(', ')', '[', ']'];

    /// <summary>
    /// The title without the edition phrase (or phrases) at its end, and the phrase, or null when the title does not end in one, or
    /// when nothing with a letter in it would be left (a title that is only "Director's Cut" keeps its words: it may be the real title).
    /// </summary>
    public static EditionSplit? Trailing(string? title)
    {
        var words = (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var plain = words.Select(Plain).ToArray();
        var end = words.Length;
        while (true)
        {
            // Words that are only punctuation ("-", ":") between the title and a phrase, or after it, are no part of either.
            var last = end;
            while (last > 0 && plain[last - 1].Length == 0)
            {
                last--;
            }

            var start = PhraseEndingAt(plain, last);
            if (start < 0)
            {
                break;
            }

            end = start;
        }

        if (end == words.Length)
        {
            return null;
        }

        var kept = words[..end].ToList();
        while (kept.Count > 0 && Plain(kept[^1]).Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        if (kept.Count == 0 || !kept.Any(word => word.Any(char.IsLetter)))
        {
            return null;
        }

        kept[^1] = kept[^1].TrimEnd(Separators).TrimEnd();
        var phrase = string.Join(' ', words[end..].Where(word => Plain(word).Length > 0)).Trim(Brackets).Trim();
        return new EditionSplit(string.Join(' ', kept), phrase);
    }

    /// <summary>Whether the text is an edition phrase (or several, an ordinal in front allowed) and nothing else.</summary>
    public static bool IsOnlyAPhrase(string? text)
    {
        var plain = (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Plain).Where(w => w.Length > 0).ToArray();
        var end = plain.Length;
        while (end > 0 && PhraseEndingAt(plain, end) is var start and >= 0)
        {
            end = start;
        }

        return plain.Length > 0 && end == 0;
    }

    /// <summary>Whether the words start with an edition phrase: "DIRECTOR'S CUT ...", "COLLECTOR'S EDITION ...".</summary>
    public static bool StartsWithAPhrase(IReadOnlyList<string> words)
    {
        var plain = words.Select(Plain).ToArray();
        return PhraseWords.Any(phrase => plain.Length >= phrase.Length && plain.AsSpan(0, phrase.Length).SequenceEqual(phrase));
    }

    /// <summary>Where the phrase that ends at <paramref name="end"/> starts (with an ordinal in front of it), or -1 when none ends there.</summary>
    private static int PhraseEndingAt(string[] plain, int end)
    {
        foreach (var phrase in PhraseWords)
        {
            var start = end - phrase.Length;
            if (start >= 0 && plain.AsSpan(start, phrase.Length).SequenceEqual(phrase))
            {
                while (start > 0 && Ordinal().IsMatch(plain[start - 1]))
                {
                    start--;
                }

                return start;
            }
        }

        return -1;
    }

    /// <summary>A word lower case, with its apostrophes dropped and anything else that is not a letter or a digit taken out.</summary>
    private static string Plain(string word) =>
        new([.. word.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    // An ordinal only: a bare number in front of a phrase is the title's own ("Copper Lantern 2 Special Edition" is the sequel's).
    [GeneratedRegex(@"^\d+(st|nd|rd|th)$", RegexOptions.CultureInvariant)]
    private static partial Regex Ordinal();
}
