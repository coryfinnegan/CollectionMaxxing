using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Scanning.Quota;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>How a run of <see cref="ResolvePendingCandidate"/> ended, for a caller that works through many codes.</summary>
public enum ResolveOutcome
{
    /// <summary>Nothing more to do for this code (resolved, given up on, or no longer waiting).</summary>
    Done,

    /// <summary>The UPC allowance is spent; the code waits for the next reset.</summary>
    QuotaSpent,
}

/// <summary>
/// Background job: resolves a code that its request ran out of time for. It runs as the person who scanned it, in their
/// library, so what it reads and writes is what they could. If the providers still can't answer, the candidate becomes an
/// Error (the person can search for it by hand) instead of waiting forever, except when the daily UPC allowance is spent: then
/// it stays Pending and is looked up again after the next reset (docs/02, "UPC quota strategy"). Safe to run twice: a candidate that is no
/// longer Pending is left alone.
/// </summary>
public sealed class ResolvePendingCandidate(
    IAppDbContext db,
    CurrentUser currentUser,
    IMembershipReader memberships,
    ISessionLock sessionLock,
    BarcodeResolver external,
    CandidateResolver resolver,
    AutoCommitT0 autoCommit,
    ScanningOptions options,
    IJobQueue jobs,
    TimeProvider clock,
    ILogger<ResolvePendingCandidate> logger) : IJobHandler<SubmitBarcodes.ResolveJob>
{
    public async Task HandleAsync(SubmitBarcodes.ResolveJob job, CancellationToken cancellationToken) =>
        await RunAsync(job, cancellationToken);

    public async Task<ResolveOutcome> RunAsync(SubmitBarcodes.ResolveJob job, CancellationToken cancellationToken)
    {
        // A job has no signed-in caller; it takes the identity of the scan's owner, with the libraries they belong to
        // now. The payload may be days old, and someone removed from the library since must get nothing written into it.
        var current = await memberships.GetMembershipsAsync(job.UserId, cancellationToken);
        if (current.All(m => m.LibraryId != job.LibraryId))
        {
            logger.LogWarning("Dropping the resolve job for candidate {CandidateId}: the person no longer belongs to the library.", job.CandidateId);
            return ResolveOutcome.Done;
        }

        currentUser.Initialize(job.UserId, current);

        var peek = await db.ScanCandidates.AsNoTracking()
            .Where(c => c.Id == job.CandidateId && c.SessionId == job.SessionId)
            .Select(c => new { c.RawCode, c.Resolution })
            .FirstOrDefaultAsync(cancellationToken);
        if (peek is null || peek.Resolution != CandidateResolution.Pending || !Identifier.TryParse(peek.RawCode, out var identifier))
        {
            return ResolveOutcome.Done;
        }

        var local = await resolver.FindLocalAsync([identifier], cancellationToken);
        BarcodeResolution answer;
        if (local.TryGetValue(identifier.Value, out var releaseIds))
        {
            answer = CandidateResolver.LocalResolution(releaseIds);
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.JobResolveBudget);
            try
            {
                answer = await external.ResolveAsync(identifier, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                answer = BarcodeResolution.Pending([new TraceStep("budget", "timeout", (long)options.JobResolveBudget.TotalMilliseconds)]);
            }
        }

        // The answer lands under the session's lock, on rows read fresh inside it: a request that resolved the same
        // candidate while the providers were being asked wins, and this run becomes the no-op it should be.
        ResolveOutcome outcome;
        await using (await sessionLock.AcquireAsync(job.SessionId, cancellationToken))
        {
            var session = await ScanSessions.FindMineAsync(db, currentUser, job.SessionId, cancellationToken);
            var candidate = await db.ScanCandidates.FirstOrDefaultAsync(
                c => c.Id == job.CandidateId && c.SessionId == job.SessionId, cancellationToken);
            if (session is null || session.Status != ScanSessionStatus.Open || candidate is null || candidate.Resolution != CandidateResolution.Pending)
            {
                return ResolveOutcome.Done;
            }

            await resolver.ApplyAsync(job.LibraryId, candidate, answer, cancellationToken, fromIdentifier: true, copiesMode: session.AutoCommitCopies);
            await autoCommit.TryAddAsync(session, candidate, job.UserId, currentUser.RoleIn(job.LibraryId), cancellationToken);
            var quotaSpent = candidate.Resolution == CandidateResolution.Pending && UpcQuotaWindow.IsSpent(answer);
            if (candidate.Resolution == CandidateResolution.Pending && !quotaSpent)
            {
                candidate.Resolution = CandidateResolution.Error; // gave up; the person can search by hand
                candidate.Tier = ScanTier.T2;
                candidate.GuidanceCode = candidate.GuidanceCode is { } keep && GuidanceCodes.IsReshoot(keep)
                    ? keep
                    : GuidanceCodes.For(null, CandidateResolution.Error, candidate.Source, candidate.RawCode, quotaSpent: false);
            }

            await db.SaveChangesAsync(cancellationToken);
            if (!quotaSpent)
            {
                return ResolveOutcome.Done;
            }

            var runAfter = UpcQuotaWindow.NextResetAfter(clock.GetUtcNow());
            await jobs.EnqueueAsync(
                SubmitBarcodes.ResolveJobType, job, new JobEnqueueOptions(runAfter, UpcQuotaWindow.DedupeKey(candidate.Id, runAfter)), cancellationToken);
            outcome = ResolveOutcome.QuotaSpent;
        }

        return outcome;
    }
}
