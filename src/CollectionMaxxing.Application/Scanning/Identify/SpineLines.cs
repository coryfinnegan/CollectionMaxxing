using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// The other lines of a spine's piece, and the rule for asking the catalogue about one of them. The phone sends the one line it
/// chose as a spine's title; when that read is about to be skipped (nothing found after every retry, <see cref="UnfoundSpines"/>, or
/// an author's name that only found a book titled with it, <see cref="FragmentReads"/>) another line of the same piece is often the
/// title (OCR split the spine, or ran the title and the author together). Each such line is asked in turn, and what it finds is kept
/// only when the catalogue's best release has a creator whose name is printed on the same piece: the author is the independent
/// witness that the line is this spine's title and not a banner, a publisher or part of a neighbour's print. Pure; the retry itself is
/// <see cref="IdentifyGrounding.RetryTrimmedAsync"/>, where these lines are asked after every other attempt (one rung more of the
/// ladder that <see cref="TrimmedSpans"/> and <see cref="CreatorSwap"/> make).
/// </summary>
public static class SpineLines
{
    /// <summary>The trace step a line's find carries.</summary>
    public const string StepName = "line";

    /// <summary>The most lines one spine keeps.</summary>
    public const int MaxLines = 12;

    /// <summary>The most other lines asked for one skipped read.</summary>
    public const int MaxAsks = 3;

    /// <summary>A line with fewer letters than this is never asked.</summary>
    public const int MinLetters = 4;

    /// <summary>The lines, newline-joined into the one value a read carries (<see cref="ReadFacts.Lines"/>); null when there are none.</summary>
    public static string? Join(IEnumerable<string> lines)
    {
        var kept = lines.ToList();
        return kept.Count == 0 ? null : string.Join('\n', kept);
    }

    /// <summary>The lines the phone sent as a read keeps them: each cleaned like any field (<see cref="IdentifyCleaning.MaxFieldLength"/> characters at most), empty and repeated ones (whatever their case) left out, at most <see cref="MaxLines"/>, newline-joined (<see cref="Join"/>). Null when none are left.</summary>
    public static string? Clean(IReadOnlyList<string>? lines)
    {
        if (lines is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (IdentifyCleaning.Text(line, IdentifyCleaning.MaxFieldLength) is { } cleaned && seen.Add(cleaned))
            {
                kept.Add(cleaned);
                if (kept.Count == MaxLines)
                {
                    break;
                }
            }
        }

        return Join(kept);
    }

    /// <summary>The lines of a joined value, in order.</summary>
    public static IReadOnlyList<string> Split(string? joined) =>
        string.IsNullOrEmpty(joined) ? [] : joined.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The lines to ask, longest first by letters (in the order read between equals), at most <see cref="MaxAsks"/>. Never the title,
    /// one of its trimmed spans or the creator swap's title (<paramref name="alreadyAsked"/> are the texts the read has been asked as), a
    /// line of fewer than <see cref="MinLetters"/> letters, a banner or publisher's mark (<see cref="FragmentReads.IsNeverATitle"/>),
    /// or a name: a line whose words are all in a name the read or its best release carries (<paramref name="creators"/>).
    /// </summary>
    public static IReadOnlyList<string> ToAsk(ReadFacts facts, IEnumerable<string> alreadyAsked, IEnumerable<string?> creators)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var lines = facts.LineList;
        if (lines.Count == 0)
        {
            return [];
        }

        var excluded = alreadyAsked.Append(facts.SearchText).Append(facts.Title).Select(TitleNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
        var nameWords = creators.Append(facts.Creator).Append(facts.CreatorAsRead)
            .SelectMany(creator => TitleNormalizer.Normalize(creator).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

        var asked = new HashSet<string>(StringComparer.Ordinal);
        return
        [
            .. lines
                .Select((line, order) => (Line: line, Order: order, Normal: TitleNormalizer.Normalize(line)))
                .Where(l => l.Line.Count(char.IsLetter) >= MinLetters
                    && l.Normal.Length > 0
                    && !excluded.Contains(l.Normal)
                    && !FragmentReads.IsNeverATitle(l.Line)
                    && !l.Normal.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(nameWords.Contains))
                .OrderByDescending(l => l.Line.Count(char.IsLetter))
                .ThenBy(l => l.Order)
                .Where(l => asked.Add(l.Normal))
                .Take(MaxAsks)
                .Select(l => l.Line),
        ];
    }

    /// <summary>
    /// Whether what a line found is kept: the best release has a creator whose every word is printed on the spine's other lines
    /// (<see cref="PrintSupport.PrintsAll"/>), and the line is not that creator's name (a book titled with its author's name,
    /// <see cref="FragmentReads.NamesItsOwnCreator"/>, is the fragment this retry exists to get past).
    /// </summary>
    public static bool KeepsFind(ReadFacts facts, string line, string? bestTitle, IReadOnlyList<string> bestCreators)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (bestCreators.Count == 0 || FragmentReads.NamesItsOwnCreator(line, bestTitle, bestCreators))
        {
            return false;
        }

        // The witness is the rest of the spine: the line that was asked is the find's own words, and a name line vouches for nothing.
        var printed = PrintSupport.PrintedWords(facts.LineList.Where(other => !string.Equals(other, line, StringComparison.OrdinalIgnoreCase)));
        return bestCreators.Any(creator => PrintSupport.PrintsAll(creator, printed));
    }

    /// <summary>
    /// Whether an answer is a find of the line retry. Such an answer is offered for review and changes nothing but its own read: it
    /// contends for its place as a read that found nothing (<see cref="ReadPlaces.Of"/>) and lends no series to its neighbours
    /// (<see cref="SeriesNeighbour.Of"/>), so a right row of another engine, or a neighbour that was not going to be skipped, is never touched by it.
    /// </summary>
    public static bool FoundBy(BarcodeResolution answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return answer.FoundByTrimmedSpan && answer.Steps.Any(step => step.Provider == StepName);
    }
}
