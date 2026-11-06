using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>One read as the unfound-spine rule judges it: the profile it was scored with, and its final verdict.</summary>
public readonly record struct UnfoundRead(ScoringProfileKind Profile, ResolutionVerdict Verdict);

/// <summary>
/// A shelf spine the catalogue found nothing for. Once every retry in the pipeline has run (possessive, trimmed spans, the next
/// sequel number, the creator swap) and a spine read's final verdict is still NotFound, there is nothing to offer the person for
/// review: it is skipped with its own step, so its row says why. Like a repeat (<see cref="RepeatReads"/>) it is skipped, never
/// deleted and never auto-added, and the person can still un-skip it (some are real spines whose title OCR garbled). Only the
/// <see cref="ScoringProfileKind.ShelfSpine"/> profile is judged: a cover read that found nothing still shows, and so does a
/// read that is still Pending (the pending job resolves it) or Ambiguous (it has options to pick from). Pure; the live
/// <c>/identify</c> path and the eval dry run both ask this one rule, so the scorer sees what a person scanning would have seen.
/// </summary>
public static class UnfoundSpines
{
    /// <summary>The trace step a skipped unfound spine carries, so its row says why it was skipped.</summary>
    public const string StepName = "unfound-spine";

    /// <summary>The positions (ascending) of the reads that are spine reads with a final verdict of NotFound.</summary>
    public static IReadOnlyList<int> Of(IReadOnlyList<UnfoundRead> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        var unfound = new List<int>();
        for (var i = 0; i < reads.Count; i++)
        {
            if (reads[i].Profile == ScoringProfileKind.ShelfSpine && reads[i].Verdict == ResolutionVerdict.NotFound)
            {
                unfound.Add(i);
            }
        }

        return unfound;
    }
}
