using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// Background job: resolves an item that its <c>/identify</c> request ran out of time for. It runs as the person who scanned
/// it, in their library, so what it reads and writes is what they could. It asks again from the read kept on the candidate
/// (no phone needed). If the providers still can't answer, the candidate becomes an Error (the person can search by hand)
/// instead of waiting forever. Safe to run twice: a candidate that is no longer Pending is left alone, and a decision the
/// person already made is never overwritten (<see cref="CandidateResolver.ApplyAsync"/>).
/// </summary>
public sealed class ResolvePendingIdentify(
    IAppDbContext db,
    CurrentUser currentUser,
    IMembershipReader memberships,
    IdentifyGrounding grounding,
    CandidateResolver resolver,
    ScanningOptions options,
    ILogger<ResolvePendingIdentify> logger) : IJobHandler<IdentifyItems.ResolveJob>
{
    public async Task HandleAsync(IdentifyItems.ResolveJob job, CancellationToken cancellationToken)
    {
        // A job has no signed-in caller; it takes the identity of the scan's owner, with the libraries they belong to
        // now. The payload may be days old, and someone removed from the library since must get nothing written into it.
        var current = await memberships.GetMembershipsAsync(job.UserId, cancellationToken);
        if (current.All(m => m.LibraryId != job.LibraryId))
        {
            logger.LogWarning("Dropping the identify job for candidate {CandidateId}: the person no longer belongs to the library.", job.CandidateId);
            return;
        }

        currentUser.Initialize(job.UserId, current);

        var candidate = await db.ScanCandidates.FirstOrDefaultAsync(
            c => c.Id == job.CandidateId && c.SessionId == job.SessionId, cancellationToken);
        if (candidate is null || candidate.Resolution != CandidateResolution.Pending)
        {
            return;
        }

        var open = await db.ScanSessions.AnyAsync(s => s.Id == job.SessionId && s.Status == ScanSessionStatus.Open, cancellationToken);
        if (!open)
        {
            return;
        }

        var (facts, profile) = StoredRead.From(candidate);
        var answer = await grounding.GroundAsync(facts, profile, options.JobResolveBudget, cancellationToken);
        await resolver.ApplyAsync(job.LibraryId, candidate, answer, cancellationToken);
        if (candidate.Resolution == CandidateResolution.Pending)
        {
            candidate.Resolution = CandidateResolution.Error; // gave up; the person can search by hand
            candidate.GuidanceCode = candidate.GuidanceCode is { } keep && GuidanceCodes.IsReshoot(keep)
                ? keep
                : GuidanceCodes.For(null, CandidateResolution.Error, candidate.Source, candidate.RawCode, quotaSpent: false);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
