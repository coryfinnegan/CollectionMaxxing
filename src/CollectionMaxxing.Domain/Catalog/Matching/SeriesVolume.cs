namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>A title split into its series and its volume: ("one piece", "vol 1"). The volume is "" when the title names none.</summary>
public sealed record SeriesVolume(string Series, string Volume);

/// <summary>
/// The series-volume veto's eyes (docs/16 §6): which titles name the same series with a different volume, so a
/// match must be picked by the person and never auto-added. Pure: no I/O, no clock, no randomness.
/// <para>
/// The volume is whatever trails the series: a marked number ("Vol. 1", "Part Two", "Book 3", "#4"), a bare
/// trailing number ("Final Fantasy 7"), a year ("Madden NFL 2005"), or a bundle word ("Omnibus 1", "Box Set 2").
/// Titles compare normalised (<see cref="TitleNormalizer"/>), so case, punctuation and articles do not matter.
/// </para>
/// </summary>
public static class SeriesVolumes
{
    private static readonly HashSet<string> Markers = new(StringComparer.Ordinal)
    {
        "vol", "volume", "v", "book", "bk", "tome", "part", "pt", "chapter", "ch",
        "number", "no", "num", "episode", "ep", "season", "series", "set",
    };

    private static readonly HashSet<string> Bundles = new(StringComparer.Ordinal)
    {
        "omnibus", "boxset", "collection", "complete", "anthology", "trilogy", "bundle",
    };

    private static readonly HashSet<(string, string)> BundlePairs = new()
    {
        ("box", "set"), ("complete", "series"), ("complete", "collection"),
    };

    /// <summary>One spelling per volume word, so "Vol. 1" and "Volume 1" are the same volume.</summary>
    private static readonly Dictionary<string, string> Canonical = new(StringComparer.Ordinal)
    {
        ["vol"] = "vol",
        ["volume"] = "vol",
        ["v"] = "vol",
        ["book"] = "book",
        ["bk"] = "book",
        ["tome"] = "book",
        ["part"] = "part",
        ["pt"] = "part",
        ["chapter"] = "chapter",
        ["ch"] = "chapter",
        ["number"] = "no",
        ["no"] = "no",
        ["num"] = "no",
        ["episode"] = "episode",
        ["ep"] = "episode",
        ["boxset"] = "boxset",
        ["complete"] = "complete",
    };

    /// <summary>
    /// Splits a title into its series and its volume. Null when there is no series to speak of: a blank title, or a
    /// volume on its own ("Vol. 1", "2005"), which could belong to anything.
    /// </summary>
    public static SeriesVolume? Parse(string? title)
    {
        var normalized = TitleNormalizer.Normalize(title);
        if (normalized.Length == 0)
        {
            return null;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = VolumeStart(tokens);
        string series;
        string volume;
        if (start >= 0)
        {
            series = string.Join(' ', tokens[..start]);
            volume = CanonicalVolume(tokens[start..]);
        }
        else
        {
            var end = tokens.Length;
            while (end > 0 && IsDigits(tokens[end - 1]))
            {
                end--;
            }

            series = string.Join(' ', tokens[..end]);
            volume = string.Join(' ', tokens[end..]);
        }

        return series.Length == 0 ? null : new SeriesVolume(series, volume);
    }

    /// <summary>
    /// True when both titles name the same series but different volumes: the person must pick, never the auto-add.
    /// A title the parser cannot split never vetoes (there is no series to clash on), and neither do identical titles.
    /// </summary>
    public static bool Vetoes(string? first, string? second)
    {
        var a = Parse(first);
        var b = Parse(second);
        return a is not null && b is not null
            && a.Series == b.Series
            && a.Volume != b.Volume;
    }

    /// <summary>
    /// The volume in one spelling: the first word canonical ("Volume" is "vol", "Box Set" is "boxset", "Complete
    /// Series" is "complete"), the rest (usually the number) as read.
    /// </summary>
    private static string CanonicalVolume(string[] volume)
    {
        if (volume.Length >= 2 && BundlePairs.Contains((volume[0], volume[1])))
        {
            var head = volume[0] == "box" ? "boxset" : "complete";
            return volume.Length == 2 ? head : head + " " + string.Join(' ', volume[2..]);
        }

        if (volume.Length >= 1 && Canonical.TryGetValue(volume[0], out var first))
        {
            return volume.Length == 1 ? first : first + " " + string.Join(' ', volume[1..]);
        }

        return string.Join(' ', volume);
    }

    /// <summary>The first token of the volume: the earliest marker, bundle word or bundle pair. -1 when the title names none.</summary>
    private static int VolumeStart(string[] tokens)
    {
        var start = -1;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (Markers.Contains(tokens[i]) || Bundles.Contains(tokens[i])
                || (i + 1 < tokens.Length && BundlePairs.Contains((tokens[i], tokens[i + 1]))))
            {
                start = i;
                break;
            }
        }

        return start;
    }

    private static bool IsDigits(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
