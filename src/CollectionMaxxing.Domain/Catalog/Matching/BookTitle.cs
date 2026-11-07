using System.Text.RegularExpressions;

namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// A Google Books title as a spine prints it. The provider runs the series into the title ("Harbor Light (Maple Hollow,
/// Book 1)") and carries a marketing tagline as the subtitle of some editions, and no spine prints either, so the right
/// work would sit under a title nobody reads. A short real subtitle stays, and a title that is only a series marker keeps
/// its words.
/// </summary>
public static partial class BookTitle
{
    private const int TaglineWords = 8;

    [GeneratedRegex(@"\s*\((?:[^()]*?[,\s]\s*)?(?:Book|Volume|Vol\.?|#)\s*\d+\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex SeriesMarker();

    /// <summary>The title without a trailing series marker and without a tagline subtitle, then joined as <see cref="GenreSubtitle.Join"/> does.</summary>
    public static string Clean(string title, string? subtitle)
    {
        ArgumentNullException.ThrowIfNull(title);
        var bare = SeriesMarker().Replace(title, string.Empty);
        if (string.IsNullOrWhiteSpace(bare))
        {
            bare = title;
        }

        return GenreSubtitle.Join(bare, subtitle is not null && IsTagline(subtitle) ? null : subtitle);
    }

    /// <summary>A subtitle that holds a <c>|</c> or <c>»</c>, ends in an ellipsis, or runs to 8 words or more.</summary>
    public static bool IsTagline(string subtitle)
    {
        var text = subtitle.Trim();
        return text.Contains('|') || text.Contains('»') || text.EndsWith("..", StringComparison.Ordinal) || text.EndsWith('…')
            || text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= TaglineWords;
    }
}
