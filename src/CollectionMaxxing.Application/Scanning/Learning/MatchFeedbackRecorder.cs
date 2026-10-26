using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Learning;

/// <summary>
/// The learning loop's second half (docs/02, section 8): when the person picks a different release than the one we ranked
/// first, that is written to <c>match_feedback</c> (it feeds the evals). One row per candidate: choosing again replaces it,
/// and going back to what we ranked first removes it, since then there is nothing to learn from.
/// </summary>
public sealed class MatchFeedbackRecorder(IAppDbContext db, IIdGenerator ids, TimeProvider clock)
{
    /// <summary>Saves nothing; the caller saves.</summary>
    public async Task RecordPickAsync(ScanCandidate candidate, Guid? rankedFirst, Guid picked, CancellationToken cancellationToken)
    {
        var previous = await db.MatchFeedback.Where(f => f.CandidateId == candidate.Id).ToListAsync(cancellationToken);
        db.MatchFeedback.RemoveRange(previous);
        if (rankedFirst is null || rankedFirst == picked)
        {
            return;
        }

        db.MatchFeedback.Add(new MatchFeedback
        {
            Id = ids.NewId(),
            LibraryId = candidate.LibraryId,
            CandidateId = candidate.Id,
            RankedFirstReleaseId = rankedFirst,
            PickedReleaseId = picked,
            CreatedAt = clock.GetUtcNow(),
        });
    }

    /// <summary>
    /// A confirmation where we ranked nothing usable (#345): the person made <paramref name="picked"/> from the guess, or
    /// resolved to it. Recorded with no ranked-first, replacing any earlier row for the candidate. Saves nothing; the caller saves.
    /// </summary>
    public async Task RecordConfirmedAsync(ScanCandidate candidate, Guid picked, CancellationToken cancellationToken)
    {
        var previous = await db.MatchFeedback.Where(f => f.CandidateId == candidate.Id).ToListAsync(cancellationToken);
        db.MatchFeedback.RemoveRange(previous);
        db.MatchFeedback.Add(new MatchFeedback
        {
            Id = ids.NewId(),
            LibraryId = candidate.LibraryId,
            CandidateId = candidate.Id,
            RankedFirstReleaseId = null,
            PickedReleaseId = picked,
            CreatedAt = clock.GetUtcNow(),
        });
    }
}
