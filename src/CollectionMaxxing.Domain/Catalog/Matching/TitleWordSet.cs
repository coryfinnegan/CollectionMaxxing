using System.Text.RegularExpressions;

namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// The words of a title that tell one title from another: lowercased, split on anything that is not a letter or a digit, with the
/// one-letter words and the stop words left out. The same definition as the Application's <c>PrintSupport.TitleWords</c> (a test pins
/// that they agree); the Domain cannot reference the Application, so it keeps its own.
/// </summary>
public static partial class TitleWordSet
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "to", "in", "on", "for", "at", "by", "with", "from", "or", "is",
    };

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex NotLetterOrDigit();

    public static HashSet<string> Of(string? text) => string.IsNullOrEmpty(text)
        ? []
        : [.. NotLetterOrDigit().Split(text.ToLowerInvariant()).Where(word => word.Length >= 2 && !StopWords.Contains(word))];
}
