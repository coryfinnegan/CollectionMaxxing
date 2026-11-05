namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>A read title without its sequel number: the title to ask with, and the number dropped to make it (for the trace).</summary>
public sealed record SequelNumberDrop(string Title, string Token);

/// <summary>
/// The sequel number of a read title, and the title without it. A sequel number is the commonest thing a cover or a spine gets
/// wrong ("WING COMMANDER II THE PRICE OF FREEDOM" is the game "Wing Commander IV: The Price of Freedom"), and a title with a
/// number the catalogue does not hold finds nothing at all, while the same title without the number names the game. Pure; the
/// retry itself is <see cref="IdentifyGrounding.RetryTrimmedAsync"/>, where this is asked after the trimmed spans and before the
/// creator swap (<see cref="TrimmedSpans"/>, <see cref="CreatorSwap"/>).
/// </summary>
/// <remarks>
/// A sequel number is a whole word that is a roman numeral from II to XX (whatever its case), or an Arabic number from 2 to 20
/// without a leading zero. Never "I" (a letter, a pronoun: "I, Robot"), never a year ("1984", "Blade Runner 2049"), never a number
/// glued to letters or other digits ("Catch-22", "3D", "2nd"). Punctuation around the word is no part of it ("IV:", "(2)").
/// </remarks>
public static class SequelNumber
{
    /// <summary>The title that is left must have at least this many words: a single word finds anything.</summary>
    public const int MinWordsLeft = 2;

    private const int LowestArabic = 2;

    private const int HighestArabic = 20;

    private const int MostDigits = 2;

    private static readonly HashSet<string> Roman = new(StringComparer.OrdinalIgnoreCase)
    {
        "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX",
    };

    /// <summary>
    /// The title without its sequel number, or null when there is nothing to ask: no title, no sequel number in it, more than one
    /// (it cannot be told which is the sequel's: "Harry Potter 7 8", "2 Fast 2 Furious"), or fewer than <see cref="MinWordsLeft"/>
    /// words once it is dropped. The other words are kept as they were read, in order.
    /// </summary>
    public static SequelNumberDrop? For(string? title)
    {
        var words = (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var at = -1;
        for (var i = 0; i < words.Length; i++)
        {
            if (!IsToken(words[i]))
            {
                continue;
            }

            if (at >= 0)
            {
                return null;
            }

            at = i;
        }

        if (at < 0 || words.Length - 1 < MinWordsLeft)
        {
            return null;
        }

        return new SequelNumberDrop(string.Join(' ', words.Where((_, i) => i != at)), Core(words[at]).ToString());
    }

    /// <summary>Whether a whole word of a title is a sequel number: a roman numeral from II to XX, or an Arabic number from 2 to 20.</summary>
    public static bool IsToken(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var core = Core(word);
        if (core.IsEmpty)
        {
            return false;
        }

        if (Roman.GetAlternateLookup<ReadOnlySpan<char>>().Contains(core))
        {
            return true;
        }

        return core.Length <= MostDigits
            && core[0] != '0'
            && core.ToString().All(char.IsAsciiDigit)
            && int.Parse(core, System.Globalization.CultureInfo.InvariantCulture) is >= LowestArabic and <= HighestArabic;
    }

    /// <summary>The word without the punctuation at its ends ("IV:" is "IV"); what is left holds its letters and digits and whatever stands between them.</summary>
    private static ReadOnlySpan<char> Core(string word)
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

        return word.AsSpan(start, end - start);
    }
}
