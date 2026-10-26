using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// The session's live counters (docs/16 §5). Every candidate lands in exactly one bucket, and every open provisional
/// counts as captured and to review, so at rest <c>Captured == Added + ToReview + Owned + Skipped</c>.
/// <c>Skipped</c> is not shown on the phone's bar, but the reconciliation needs it named.
/// </summary>
public sealed record SessionCountersDto(int Captured, int Added, int ToReview, int Owned, int Skipped);

/// <summary>One snap's state in the session, from its candidates. The phone's strip mirrors these for its own snaps.</summary>
public enum SessionSegmentState
{
    /// <summary>At least one candidate is still resolving.</summary>
    Resolving,

    /// <summary>Settled, but something needs human eyes (ambiguous, not found, error).</summary>
    NeedsEye,

    /// <summary>Everything matched and nothing needs a decision.</summary>
    Resolved,

    /// <summary>Every candidate decided or turned into an item.</summary>
    Reviewed,
}

/// <summary>One snap sent to the session, in shoot order, with the candidates it produced.</summary>
public sealed record SessionSegmentDto(
    Guid SnapId,
    ShotKind ShotKind,
    SnapTrigger Trigger,
    int ItemCount,
    SessionSegmentState State,
    DateTimeOffset CreatedAt,
    IReadOnlyList<Guid> CandidateIds);

/// <summary>Counts the session's candidates into the live counters and per-snap segments (docs/16 §5).</summary>
public static class SessionCounters
{
    /// <param name="openProvisionals">How many of the session's provisional records still need attention: each counts as one captured segment waiting for review.</param>
    public static SessionCountersDto Count(IEnumerable<ScanCandidate> candidates, int openProvisionals = 0)
    {
        var captured = 0;
        var added = 0;
        var toReview = 0;
        var owned = 0;
        var skipped = 0;
        foreach (var candidate in candidates)
        {
            captured++;
            if (candidate.CreatedItemId is not null)
            {
                added++;
            }
            else if (candidate.AlreadyOwnedCount > 0)
            {
                // An owned hit answered on its card, including reject-and-drop ("Skip"): never a failure.
                owned++;
            }
            else if (candidate.Decision == CandidateDecision.Rejected)
            {
                skipped++;
            }
            else
            {
                toReview++;
            }
        }

        captured += openProvisionals;
        toReview += openProvisionals;

        return new SessionCountersDto(captured, added, toReview, owned, skipped);
    }

    public static IReadOnlyList<SessionSegmentDto> Segments(IEnumerable<ScanSnap> snaps, IEnumerable<ScanCandidate> candidates)
    {
        var bySnap = candidates
            .Where(c => c.SnapId is not null)
            .GroupBy(c => c.SnapId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Seq).ToList());
        return snaps
            .OrderBy(s => s.CreatedAt)
            .Select(s => new SessionSegmentDto(
                s.Id,
                s.ShotKind,
                s.Trigger,
                s.ItemCount,
                StateOf(bySnap.TryGetValue(s.Id, out var mine) ? mine : []),
                s.CreatedAt,
                bySnap.TryGetValue(s.Id, out var own) ? own.Select(c => c.Id).ToList() : []))
            .ToList();
    }

    private static SessionSegmentState StateOf(List<ScanCandidate> candidates)
    {
        if (candidates.Count == 0 || candidates.Any(c => c.Resolution == CandidateResolution.Pending))
        {
            return SessionSegmentState.Resolving;
        }

        if (candidates.All(c => c.CreatedItemId is not null || c.Decision != CandidateDecision.Undecided))
        {
            return SessionSegmentState.Reviewed;
        }

        if (candidates.Any(c => c.Resolution is CandidateResolution.Ambiguous or CandidateResolution.NotFound or CandidateResolution.Error))
        {
            return SessionSegmentState.NeedsEye;
        }

        return SessionSegmentState.Resolved;
    }
}
