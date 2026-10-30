using System.Globalization;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// A title's sequel numbers written the other way round, for a search: "Camp 3" as "Camp III" and "Camp III" as "Camp 3". A
/// catalogue spells a sequel one way and a cover the other, and a text search does not know they are the same word.
/// </summary>
internal static class TitleNumerals
{
    private static readonly string[] Roman =
    [
        "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX",
    ];

    // II to XX, leaving a lone I and a lone X out: they are far more often a word ("I Am Legend", "Malcolm X", "Mega Man X")
    // than a one or a ten, and a search for "Mega Man 10" finds a different game.
    private static readonly Dictionary<string, string> Arabic = Roman
        .Select((numeral, index) => (numeral, number: index + 1))
        .Where(pair => pair.numeral is not ("I" or "X"))
        .ToDictionary(pair => pair.numeral, pair => pair.number.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The words with every standalone number 1 to 20 written as a Roman numeral, and the words with every standalone Roman
    /// numeral II to XX written as a number: the first only when there is such a number, the second only when there is such
    /// a numeral. "Standalone" is a word of its own: "3D", "Se7en", "2049" and "22" are left alone.
    /// </summary>
    public static IEnumerable<string[]> Swapped(string[] words)
    {
        if (Rewrite(words, ToRoman) is { } roman)
        {
            yield return roman;
        }

        if (Rewrite(words, ToArabic) is { } arabic)
        {
            yield return arabic;
        }
    }

    private static string[]? Rewrite(string[] words, Func<string, string?> swap)
    {
        string[]? swapped = null;
        for (var i = 0; i < words.Length; i++)
        {
            if (swap(words[i]) is { } other)
            {
                swapped ??= [.. words];
                swapped[i] = other;
            }
        }

        return swapped;
    }

    private static string? ToRoman(string word)
    {
        var isNumber = word.Length is 1 or 2 && word.All(char.IsAsciiDigit) && word[0] != '0';
        var number = isNumber ? int.Parse(word, CultureInfo.InvariantCulture) : 0;
        return number is >= 1 and <= 20 ? Roman[number - 1] : null;
    }

    private static string? ToArabic(string word) => Arabic.GetValueOrDefault(word);
}
