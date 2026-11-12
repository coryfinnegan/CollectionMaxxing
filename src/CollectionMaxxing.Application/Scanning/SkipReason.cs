using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Application.Scanning.ShelfReading;
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

    /// <summary>The library already owns this release, and the session was not adding copies.</summary>
    Owned,

    /// <summary>The release is on the library's wishlist: Got it, on its card, is how it comes in.</summary>
    Wishlist,

    /// <summary>The server's own read of the shelf left this phone read out (a fragment, a one-word or off-kind read, or one only the phone's model made).</summary>
    ServerDidNotSee,
}

internal static class SkipReasons
{
    /// <summary>
    /// Why the server rejected a candidate on its own: its skip step, or an owned or wishlisted match. Null when the person decided
    /// (a decision clears the trace's <c>AutoDecision</c>, so a put-back and a skip by the person is theirs) or the row is not rejected.
    /// </summary>
    public static SkipReason? Of(CandidateDecision decision, int alreadyOwnedCount, StoredTrace trace)
    {
        if (decision != CandidateDecision.Rejected || trace.AutoDecision != CandidateDecision.Rejected)
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

            if (step.Provider == ServerDidNotSee.StepName)
            {
                return SkipReason.ServerDidNotSee;
            }
        }

        if (alreadyOwnedCount > 0)
        {
            return SkipReason.Owned;
        }

        return trace.WishlistItemId is not null ? SkipReason.Wishlist : null;
    }
}
