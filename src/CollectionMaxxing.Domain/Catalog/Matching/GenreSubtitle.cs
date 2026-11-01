using System.Buffers;

namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// A subtitle that is only a genre label. The book providers append one to a title the cover and the catalogue carry
/// alone ("Dune: A Novel", "Der Vorleser: Roman"), and comparing the whole of it with a spine read costs the match, so a
/// release is stored and offered under its title alone. A real subtitle stays: it is what tells two releases of one title
/// apart ("Metroid Prime 2: Echoes"), and a read that is only the main title already earns
/// <see cref="TitleNormalizer.MainTitleCredit"/> for it.
/// </summary>
public static class GenreSubtitle
{
    /// <summary>
    /// The labels that are dropped. Narrow on purpose: a word that can be a real subtitle ("Stories", "Poems",
    /// "A Memoir") is not a label here, so no release loses a subtitle its catalogue entry means to carry. Widen it
    /// only on a measurement that says the next label is worth it.
    /// </summary>
    private static readonly HashSet<string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        "a novel", "novel", "a novella", "novella", "roman", "ein roman", "un roman",
    };

    /// <summary>Where a subtitle starts. A bracketed one is handled on its own, because it also has to close.</summary>
    private static readonly string[] Markers = [":", " - ", " – ", " — "];

    /// <summary>The first character of every marker, for the quick "this title has no subtitle at all" test.</summary>
    private static readonly SearchValues<char> MarkerStarts = SearchValues.Create([':', '-', '–', '—', '(']);

    /// <summary>
    /// A provider's title and its subtitle as one title, dropping a genre label whichever field carries it: a provider
    /// answers the same book now with a <c>subtitle</c> of "A Novel" and now with that label run into the title. A
    /// blank subtitle is no subtitle.
    /// </summary>
    public static string Join(string title, string? subtitle)
    {
        ArgumentNullException.ThrowIfNull(title);
        var joined = string.IsNullOrWhiteSpace(subtitle) || IsOnlyALabel(subtitle) ? title : $"{title}: {subtitle}";
        return WithoutLabel(joined) ?? joined;
    }

    /// <summary>
    /// True when the text is nothing but a genre label, whatever its case, its spacing or a closing period.
    /// Blank is not a label: there is nothing to drop.
    /// </summary>
    public static bool IsOnlyALabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Labels.Contains(string.Join(' ', words).TrimEnd('.'));
    }

    /// <summary>
    /// The title without a trailing genre-label subtitle: "Dune: A Novel" and "Dune (A Novel)" give "Dune". The title
    /// itself when it has none, when the label is the whole of it ("A Novel" is a title like any other), or when it is
    /// null or blank.
    /// </summary>
    public static string? WithoutLabel(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        // Every label sits behind a subtitle marker, so a title carrying none of them is already its own title. Worth
        // the scan: this runs for every title the normaliser is given.
        if (title.AsSpan().IndexOfAny(MarkerStarts) < 0)
        {
            return title;
        }

        var trimmed = title.TrimEnd();
        var open = trimmed.Length > 0 && trimmed[^1] == ')' ? trimmed.LastIndexOf('(') : -1;
        if (open > 0 && IsOnlyALabel(trimmed[(open + 1)..^1]))
        {
            return Kept(trimmed[..open]) ?? title;
        }

        foreach (var marker in Markers)
        {
            var at = trimmed.LastIndexOf(marker, StringComparison.Ordinal);
            if (at > 0 && IsOnlyALabel(trimmed[(at + marker.Length)..]))
            {
                return Kept(trimmed[..at]) ?? title;
            }
        }

        return title;
    }

    private static string? Kept(string head) => head.Trim() is { Length: > 0 } kept ? kept : null;
}
