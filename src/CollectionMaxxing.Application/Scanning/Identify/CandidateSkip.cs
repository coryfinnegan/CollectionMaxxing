using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// The server's own skip of a candidate: the row stays, with its answer, so the phone's line for the read has a card and the person
/// can still un-skip it; but it defaults to skipped, in review and at commit, and is never T0 or T1, so nothing adds it by itself.
/// The trace step says why (<see cref="RepeatReads.StepName"/>, <see cref="ReadPlaces.StepName"/>, <see cref="FragmentReads.StepName"/>,
/// <see cref="UnfoundSpines.StepName"/>). Saves nothing; the caller saves.
/// </summary>
internal static class CandidateSkip
{
    public static void Apply(ScanCandidate candidate, string step)
    {
        candidate.Decision = CandidateDecision.Rejected;
        candidate.Tier = ScanTier.T2;
        var trace = CandidateJson.ReadTrace(candidate.TraceJson);
        candidate.TraceJson = CandidateJson.Serialize(trace with
        {
            Steps = [.. trace.Steps, new TraceStep(step, "skipped", 0)],
            AutoDecision = CandidateDecision.Rejected,
        });
    }
}
