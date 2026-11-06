using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>Why the server skipped a candidate on its own (<c>CandidateSkip</c>): the app maps each to its own plain words.</summary>
public enum SkipReason
{
    /// <summary>The same book was read twice and this was the weaker read.</summary>
    Repeat,

    /// <summary>The read lost its place on the shelf to another.</summary>
    Place,

    /// <summary>The read was only part of a title.</summary>
    Fragment,

    /// <summary>A shelf spine the catalogue found nothing for.</summary>
    UnfoundSpine,
}

internal static class SkipReasons
{
    /// <summary>The server's own skip step of a candidate that is still rejected; null when the person skipped it, or it is not rejected.</summary>
    public static SkipReason? Of(CandidateDecision decision, StoredTrace trace)
    {
        if (decision != CandidateDecision.Rejected)
        {
            return null;
        }

        foreach (var step in trace.Steps.Where(s => s.Outcome == "skipped"))
        {
            if (step.Provider == RepeatReads.StepName)
            {
                return SkipReason.Repeat;
            }

            if (step.Provider == ReadPlaces.StepName)
            {
                return SkipReason.Place;
            }

            if (step.Provider == FragmentReads.StepName)
            {
                return SkipReason.Fragment;
            }

            if (step.Provider == UnfoundSpines.StepName)
            {
                return SkipReason.UnfoundSpine;
            }
        }

        return null;
    }
}
