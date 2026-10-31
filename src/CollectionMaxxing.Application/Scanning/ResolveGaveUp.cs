using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// What a scanned row becomes when nothing more will be done to resolve it: an Error, the state the app already shows with a way
/// out (search by hand, shoot the cover), never a row that says "Looking up…" for days. Used by the resolve jobs in two places:
/// when the providers still can't answer (the job's normal "give up"), and when the job itself fails for good, its retries used
/// up (<see cref="Jobs.IJobFailureHandler{TPayload}"/>), so a fault inside the job can't strand the row either.
/// </summary>
internal static class ResolveGaveUp
{
    /// <summary>Marks a still-pending row as given up on. Saves nothing; the caller saves.</summary>
    public static void Apply(ScanCandidate candidate)
    {
        candidate.Resolution = CandidateResolution.Error; // gave up; the person can search by hand
        candidate.Tier = ScanTier.T2;
        candidate.GuidanceCode = candidate.GuidanceCode is { } keep && GuidanceCodes.IsReshoot(keep)
            ? keep
            : GuidanceCodes.For(null, CandidateResolution.Error, candidate.Source, candidate.RawCode, quotaSpent: false);
    }

    /// <summary>
    /// Settles the row a failed-for-good job was for, when it is still Pending. It runs with no signed-in caller, so it finds the
    /// row by the ids the job carried (library, session, row) and touches nothing else; a row that was resolved, decided or
    /// removed meanwhile is left exactly as it is. Takes the session's lock, like every other writer of the session's rows.
    /// </summary>
    public static async Task SettleAsync(
        IAppDbContext db, ISessionLock sessionLock, ILogger logger, Guid libraryId, Guid sessionId, Guid candidateId, string error,
        CancellationToken cancellationToken)
    {
        await using (await sessionLock.AcquireAsync(sessionId, cancellationToken))
        {
            var candidate = await db.ScanCandidates.IgnoreQueryFilters().FirstOrDefaultAsync(
                c => c.Id == candidateId && c.SessionId == sessionId && c.LibraryId == libraryId, cancellationToken);
            if (candidate is null || candidate.Resolution != CandidateResolution.Pending)
            {
                return;
            }

            Apply(candidate);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Gave up on scanned item {CandidateId}: its resolve job failed for good ({Error}).", candidateId, error);
        }
    }
}
