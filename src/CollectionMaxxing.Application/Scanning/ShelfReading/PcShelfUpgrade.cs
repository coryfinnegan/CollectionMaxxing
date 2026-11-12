using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Scanning.ShelfReading;

/// <summary>
/// The one rule for what the server's own shelf read adds beside what the scan already shows (ADR-033). The endpoint that reads a
/// shelf snap on the server and <see cref="EvalCombination.OfServerRead"/> (the <c>server_vision</c> row) both ask it, so the eval
/// measures exactly what the app shows, and the rule can be retuned by a deploy and a re-ground with no app build.
/// </summary>
/// <remarks>
/// A read the server made has no place on the shelf. It stands (is added, unconfirmed, for review) when no row the scan already
/// shows names the same release as its best one (its normalised title and year), and when the shelf's print backs its title
/// (<see cref="PrintSupport.Supports"/>). With no print (the phone sent no lines, or Vision read no text) there is nothing to check
/// against, so the print does not hold a read back, as in the eval row. Pure.
/// </remarks>
public static class PcShelfUpgrade
{
    /// <summary>The release a read's best option names: its normalised title and year. Equal keys are the same release.</summary>
    public readonly record struct ReleaseKey(string Title, int? Year)
    {
        /// <summary>The key of a release, or null when its title normalises to nothing (there is no release to compare).</summary>
        public static ReleaseKey? Of(string? title, int? year) =>
            title is not null && TitleNormalizer.Normalize(title) is { Length: > 0 } normal ? new ReleaseKey(normal, year) : null;
    }

    /// <summary>The shelf's own print: every word of every line sent with the snap. Null when there are none, so there is no print to check against.</summary>
    public static IReadOnlySet<string>? PrintOf(IEnumerable<string>? lines)
    {
        var list = lines?.ToList();
        return list is { Count: > 0 } ? PrintSupport.PrintedWords(list) : null;
    }

    /// <summary>
    /// Whether a read only the server made is added beside <paramref name="existing"/>, the best releases of the rows the scan already
    /// shows for this picture (a null entry is a row with no release). <paramref name="best"/> is the read's own best release and
    /// <paramref name="readTitle"/> its title as the model read it.
    /// </summary>
    public static bool Stands(ReleaseKey? best, string? readTitle, IEnumerable<ReleaseKey?> existing, IReadOnlySet<string>? printed)
    {
        if (best is { } key && existing.Any(other => other == key))
        {
            return false; // a row already has that release
        }

        return printed is null || PrintSupport.Supports(readTitle, printed);
    }
}
