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
    ISessionLock sessionLock,
    IdentifyGrounding grounding,
    CandidateResolver resolver,
    AutoCommit autoCommit,
    ReleaseCovers releaseCovers,
    ScanningOptions options,
    TimeProvider clock,
    ILogger<ResolvePendingIdentify> logger) : IJobHandler<IdentifyItems.ResolveJob>, IJobFailureHandler<IdentifyItems.ResolveJob>
{
    /// <summary>The job failed for good (retries used up): the row becomes an Error instead of waiting for days.</summary>
    public Task OnFailedForGoodAsync(IdentifyItems.ResolveJob job, string lastError, CancellationToken cancellationToken) =>
        ResolveGaveUp.SettleAsync(db, sessionLock, logger, job.LibraryId, job.SessionId, job.CandidateId, lastError, cancellationToken);

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

        var (stored, profile) = StoredRead.From(candidate);
        var facts = await WithSnapKindAsync(candidate, stored, cancellationToken);
        var answer = await grounding.GroundAsync(facts, profile, options.JobResolveBudget, cancellationToken);

        // The answer lands under the session's lock, on rows read fresh inside it: a request that resolved the same
        // candidate while the providers were being asked wins, and this run becomes the no-op it should be.
        await using (await sessionLock.AcquireAsync(job.SessionId, cancellationToken))
        {
            var session = await ScanSessions.FindMineAsync(db, currentUser, job.SessionId, cancellationToken);
            var row = await db.ScanCandidates.FirstOrDefaultAsync(
                c => c.Id == job.CandidateId && c.SessionId == job.SessionId, cancellationToken);
            if (session is null || session.Status != ScanSessionStatus.Open || row is null || row.Resolution != CandidateResolution.Pending)
            {
                return;
            }

            await resolver.ApplyAsync(job.LibraryId, row, answer, cancellationToken);
            if (job.ProofItemId is { } proofItemId)
            {
                // An edition proof finishes as its request would have: upgrade on a match of the item's own work, a
                // T2 row otherwise, never an auto-add (docs/16 §11). The role is the one the person has now (read above from
                // the memberships): a proof queued by an editor who has since been made a viewer changes nothing.
                if (!await ProofUpgrade.TryApplyAsync(db, session, row, proofItemId, currentUser.RoleIn(job.LibraryId), clock, cancellationToken)
                    && row.Resolution != CandidateResolution.Pending)
                {
                    row.Tier = ScanTier.T2;
                }
            }
            var coverFiles = new List<string>();
            if (job.ProofItemId is null)
            {
                await autoCommit.TryAddAsync(session, row, job.UserId, currentUser.RoleIn(job.LibraryId), cancellationToken, coverFiles);
            }

            if (row.Resolution == CandidateResolution.Pending)
            {
                ResolveGaveUp.Apply(row); // gave up; the person can search by hand
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                await releaseCovers.DeleteFilesAsync(coverFiles); // nothing points at the covers just downloaded
                throw;
            }
        }
    }

    /// <summary>
    /// A read that names no kind is asked again with the kind its snap agreed on, as the request would have asked it
    /// (<see cref="KindContext"/>): the snap's other rows keep the kinds they were read with. A row with no snap, or alone in
    /// it (a barcode, a bound cover), has no context.
    /// </summary>
    private async Task<ReadFacts> WithSnapKindAsync(ScanCandidate candidate, ReadFacts facts, CancellationToken cancellationToken)
    {
        if (facts.Kind is not null || candidate.SnapId is not { } snapId)
        {
            return facts;
        }

        var reads = await db.ScanCandidates.AsNoTracking()
            .Where(c => c.SessionId == candidate.SessionId && c.SnapId == snapId)
            .Select(c => c.InferredJson)
            .ToListAsync(cancellationToken);
        return KindContext.Dominant(reads.Select(StoredRead.KindOf)) is { } snapKind ? facts with { KindContext = snapKind } : facts;
    }
}
