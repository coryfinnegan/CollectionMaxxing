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
    BarcodeResolver external,
    CandidateResolver resolver,
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

        var candidate = await db.ScanCandidates.FirstOrDefaultAsync(
            c => c.Id == job.CandidateId && c.SessionId == job.SessionId, cancellationToken);
        if (candidate is null || candidate.Resolution != CandidateResolution.Pending)
        {
            return ResolveOutcome.Done;
        }

        var open = await db.ScanSessions.AnyAsync(s => s.Id == job.SessionId && s.Status == ScanSessionStatus.Open, cancellationToken);
        if (!open || !Identifier.TryParse(candidate.RawCode, out var identifier))
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

        await resolver.ApplyAsync(job.LibraryId, candidate, answer, cancellationToken);
        var quotaSpent = candidate.Resolution == CandidateResolution.Pending && UpcQuotaWindow.IsSpent(answer);
        if (candidate.Resolution == CandidateResolution.Pending && !quotaSpent)
        {
            candidate.Resolution = CandidateResolution.Error; // gave up; the person can search by hand
        }

        await db.SaveChangesAsync(cancellationToken);
        if (!quotaSpent)
        {
            return ResolveOutcome.Done;
        }

        var runAfter = UpcQuotaWindow.NextResetAfter(clock.GetUtcNow());
        await jobs.EnqueueAsync(
            SubmitBarcodes.ResolveJobType, job, new JobEnqueueOptions(runAfter, UpcQuotaWindow.DedupeKey(candidate.Id, runAfter)), cancellationToken);
        return ResolveOutcome.QuotaSpent;
    }
}
