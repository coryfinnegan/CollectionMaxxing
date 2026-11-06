using System.Text.RegularExpressions;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// Whether a shelf's own print backs up a title only the picture model read: the rule the phone applies before it adds a
/// model-only read to a shelf (<c>printSupport.ts</c>), repeated here so the <c>apple_vision</c> row the M3 gate measures
/// (<see cref="EvalCombination"/>) shows what the app shows. A change to either is a change to both.
/// </summary>
/// <remarks>
/// On a shelf shot the model's reads carry no box of their own, so a read that is none of Vision's items goes out as an item beside
/// them, and the model invents titles. A title is backed up when its words are on the shelf. The words of a title: lowercased,
/// split on anything that is not a letter or a digit, with the stop words and the words under two characters dropped. The shelf's
/// print: every word of every line Vision read on the shot, split the same way. A title word is printed when a print word equals it,
/// equals it plus or minus one trailing "s", or is one edit away (a letter changed, added or dropped) while both words are at least
/// five characters. The title is supported when it has at least one word and at least two thirds of them are printed.
/// </remarks>
public static partial class PrintSupport
{
    /// <summary>A word under this many characters says nothing about which title it is.</summary>
    private const int MinWordLength = 2;

    /// <summary>A word is read fuzzily (one wrong letter) only when it and the print word are both at least this long: "cat" and "car" are two words.</summary>
    private const int FuzzyMinLength = 5;

    /// <summary>At least <see cref="SharePrinted"/> of every <see cref="ShareOf"/> words of a title must be printed.</summary>
    private const int SharePrinted = 2;

    private const int ShareOf = 3;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "to", "in", "on", "for", "at", "by", "with", "from", "or", "is",
    };

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotLetterOrDigit();

    /// <summary>The lowercased words of a text: what is left when it is split on anything that is not a letter or a digit.</summary>
    private static IEnumerable<string> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return NotLetterOrDigit().Split(text.ToLowerInvariant()).Where(word => word.Length > 0);
    }

    /// <summary>The words of a model-read title that can back it up: the stop words and the one-letter words say nothing about which title it is.</summary>
    public static IReadOnlyList<string> TitleWords(string? title) =>
        [.. Split(title).Where(word => word.Length >= MinWordLength && !StopWords.Contains(word))];

    /// <summary>Every word of every line Vision read on the shot: all its lines, not only the ones that became a piece's title.</summary>
    public static IReadOnlySet<string> PrintedWords(IEnumerable<string> lines) =>
        lines.SelectMany(Split).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether the shelf's print backs up <paramref name="title"/>, as the model read it (not a release's title): it has at least one
    /// word, and at least two thirds of its words are printed.
    /// </summary>
    public static bool Supports(string? title, IReadOnlySet<string> printed)
    {
        var words = TitleWords(title);
        if (words.Count == 0)
        {
            return false;
        }

        var found = words.Count(word => IsPrinted(word, printed));
        return found * ShareOf >= words.Count * SharePrinted;
    }

    /// <summary>
    /// Whether every word of a person's name is on the print (the same matching as <see cref="Supports"/>, but all of them): the author check of
    /// the line retry (<see cref="Scanning.Identify.SpineLines"/>). A name must have at least two words that can be matched (stop words and
    /// single letters do not count), so a lone common word never vouches for a title.
    /// </summary>
    public static bool PrintsAll(string? name, IReadOnlySet<string> printed)
    {
        var words = TitleWords(name);
        return words.Count >= 2 && words.All(word => IsPrinted(word, printed));
    }

    /// <summary>
    /// Whether <paramref name="word"/> is on the shelf: a print word is it, is it with one trailing "s" more or less, or (when both
    /// are long enough that one wrong letter is a misread and not another word) is one edit away from it.
    /// </summary>
    private static bool IsPrinted(string word, IReadOnlySet<string> printed)
    {
        if (printed.Contains(word) || printed.Contains(word + "s") || (word.EndsWith('s') && printed.Contains(word[..^1])))
        {
            return true;
        }

        return word.Length >= FuzzyMinLength && printed.Any(candidate => candidate.Length >= FuzzyMinLength && OneEditApart(word, candidate));
    }

    /// <summary>Whether <paramref name="a"/> becomes <paramref name="b"/> by changing, adding or dropping one letter.</summary>
    private static bool OneEditApart(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        var shorter = a.Length <= b.Length ? a : b;
        var longer = a.Length <= b.Length ? b : a;
        var at = 0;
        while (at < shorter.Length && shorter[at] == longer[at])
        {
            at++;
        }

        if (at == shorter.Length && shorter.Length == longer.Length)
        {
            return true; // the same word
        }

        return shorter.Length == longer.Length
            ? shorter[(at + 1)..].SequenceEqual(longer[(at + 1)..])
            : shorter[at..].SequenceEqual(longer[(at + 1)..]);
    }
}
