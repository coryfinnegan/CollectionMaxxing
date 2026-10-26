using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// The T0 auto-add (docs/16 §6): a barcode-proof candidate becomes real items the moment it resolves, through the same
/// item shape the full commit uses (<see cref="ScanCommitItems"/>). The session stays open and keeps scanning; every
/// auto-add is undone on its own (<see cref="UndoAutoCommit"/>). Writes nothing by itself; the caller saves.
/// </summary>
public sealed class AutoCommitT0(IAppDbContext db, IIdGenerator ids, TimeProvider clock, BarcodeLearner learner)
{
    /// <summary>
    /// Adds the candidate's items when every gate allows it: tier T0, the session's tiers name T0, the caller may add to
    /// the library, the session is still open, the person did not already reject it, and the candidate was not added
    /// already. Returns the created item ids, empty when the candidate stays for review.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> TryAddAsync(
        ScanSession session, ScanCandidate candidate, Guid userId, LibraryRole? role, CancellationToken cancellationToken)
    {
        if (candidate.Tier != ScanTier.T0
            || !session.AutoCommits(ScanTier.T0)
            || role is not (LibraryRole.Owner or LibraryRole.Editor)
            || session.Status != ScanSessionStatus.Open
            || candidate.Decision == CandidateDecision.Rejected
            || candidate.CreatedItemId is not null
            || candidate.MatchedReleaseId is null)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        var releaseFor = await new EditedReleases(db, ids).ResolveAsync(session, [candidate], now, cancellationToken);
        var created = new List<Guid>();
        for (var copy = 0; copy < candidate.Copies; copy++)
        {
            var item = ScanCommitItems.Build(
                ids.NewId(),
                session.LibraryId,
                releaseFor.ForCandidate[candidate.Id],
                session.DefaultStatus ?? ItemStatus.Owned,
                condition: null,
                ScanCommitItems.CompletenessOf(candidate),
                session.DefaultLocationId,
                CoverChoice.Release,
                photoId: null,
                ScanCommitItems.ViaOf(candidate.Source),
                userId,
                now);
            db.Items.Add(item);
            created.Add(item.Id);
            if (copy == 0)
            {
                candidate.CreatedItemId = item.Id;
            }
        }

        candidate.AutoCommittedItemIds = [.. created];
        session.AutoCommittedItemIds = [.. session.AutoCommittedItemIds ?? [], .. created];
        await learner.LearnAsync(session.Id, [candidate], cancellationToken); // the next scan of this barcode is a local hit
        return created;
    }
}
