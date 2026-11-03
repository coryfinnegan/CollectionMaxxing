namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>A read title without its possessive name: the title to ask with, and the words dropped to make it (for the trace).</summary>
public sealed record PossessiveDrop(string Title, string Dropped);

/// <summary>
/// The possessive name a cover prints in front of its title, and the title without it. Covers are full of them ("Walt Disney's",
/// "Steven Spielberg's", "Jim Henson's"), often with a marketing word after ("Masterpiece", "Classic"), and the model reads the
/// whole line as the title: "WALT DISNEY'S MASTERPIECE PINOCCHIO" found the collection "Walt Disney Masterpieces" and never the
/// film. Pure; the retry itself is <see cref="IdentifyGrounding.RetryTrimmedAsync"/>, where this is asked first.
/// </summary>
/// <remarks>
/// The prefix is one to <see cref="MaxNameWords"/> capitalised words, the last of which ends in 's (or a curly apostrophe and s), and then one marketing
/// word if there is one. At least one word must be left after it, and a lone marketing word is no title ("Disney's Collection"
/// asks nothing). A leading article is no name: "The King's Speech" and "A Bug's Life" are titles, not a person's. The possessive
/// can still be the real title ("Bram Stoker's Dracula", "Ender's Game"); this only says what is left without it, and the caller
/// keeps what the whole read found.
/// </remarks>
public static class PossessivePrefix
{
    /// <summary>The most words of a name before its possessive: "Steven Spielberg's" is two, "Sir Arthur Conan Doyle's" is too many.</summary>
    public const int MaxNameWords = 3;

    /// <summary>The words that follow a possessive name on a cover and are no part of the title.</summary>
    private static readonly HashSet<string> Marketing = new(StringComparer.OrdinalIgnoreCase)
    {
        "masterpiece", "masterpieces", "classic", "classics", "presents", "present", "collection", "original", "special",
    };

    private static readonly HashSet<string> Articles = new(StringComparer.OrdinalIgnoreCase) { "a", "an", "the" };

    /// <summary>
    /// The title without its possessive name (and the marketing word after it), or null when there is nothing to ask: no title, no
    /// possessive name at its front, or no word left once it is dropped. The other words are kept as they were read, in order.
    /// </summary>
    public static PossessiveDrop? For(string? title)
    {
        var words = (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var end = EndOfName(words);
        var left = words.Length - (end + 1);
        if (end < 0 || left == 0)
        {
            return null;
        }

        var dropped = end + 1;
        if (Marketing.Contains(Core(words[dropped])))
        {
            if (left == 1)
            {
                return null; // a lone marketing word is no title to search for
            }

            dropped++;
        }

        return new PossessiveDrop(string.Join(' ', words[dropped..]), string.Join(' ', words[..dropped]));
    }

    /// <summary>The position of the word that ends the name (the first possessive one), or -1 when the title does not start with a possessive name.</summary>
    private static int EndOfName(string[] words)
    {
        for (var i = 0; i < Math.Min(MaxNameWords, words.Length); i++)
        {
            if (!char.IsUpper(words[i][0]) || (i == 0 && Articles.Contains(words[i])))
            {
                return -1;
            }

            if (IsPossessive(words[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A word that ends in 's (or a curly apostrophe and s) after at least one letter ("DISNEY'S"), whatever its case.</summary>
    private static bool IsPossessive(string word) =>
        word.Length >= 3
        && (word[^2] == '\'' || word[^2] == '’')
        && char.ToLowerInvariant(word[^1]) == 's'
        && char.IsLetter(word[^3]);

    /// <summary>The word without the punctuation at its ends ("MASTERPIECE:" is "MASTERPIECE").</summary>
    private static string Core(string word) => word.Trim(word.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray());
}
