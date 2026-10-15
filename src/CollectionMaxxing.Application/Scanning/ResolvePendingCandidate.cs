using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// Background job: resolves a code that its request ran out of time for. It runs as the person who scanned it, in their
/// library, so what it reads and writes is what they could. If the providers still can't answer, the candidate becomes an
/// Error (the person can search for it by hand) instead of waiting forever. Safe to run twice: a candidate that is no
/// longer Pending is left alone.
/// </summary>
public sealed class ResolvePendingCandidate(
    IAppDbContext db,
    CurrentUser currentUser,
    BarcodeResolver external,
    CandidateResolver resolver,
    ScanningOptions options) : IJobHandler<SubmitBarcodes.ResolveJob>
{
    public async Task HandleAsync(SubmitBarcodes.ResolveJob job, CancellationToken cancellationToken)
    {
        // A job has no signed-in caller; it takes the identity of the scan's owner, with the least role that can see the session.
        currentUser.Initialize(job.UserId, [new Membership(job.LibraryId, LibraryRole.Viewer)]);

        var candidate = await db.ScanCandidates.FirstOrDefaultAsync(
            c => c.Id == job.CandidateId && c.SessionId == job.SessionId, cancellationToken);
        if (candidate is null || candidate.Resolution != CandidateResolution.Pending)
        {
            return;
        }

        var open = await db.ScanSessions.AnyAsync(s => s.Id == job.SessionId && s.Status == ScanSessionStatus.Open, cancellationToken);
        if (!open || !Identifier.TryParse(candidate.RawCode, out var identifier))
        {
            return;
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
        if (candidate.Resolution == CandidateResolution.Pending)
        {
            candidate.Resolution = CandidateResolution.Error; // gave up; the person can search by hand
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
