using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// The auto-add (docs/16 §6): a T0 barcode-proof or T1 high-confidence candidate becomes real items the moment it
/// resolves, through the same item shape the full commit uses (<see cref="ScanCommitItems"/>), with the same cover
/// step: the release gets the cover its provider named (or the reason it has none) before the items are built, so an
/// auto-added book shows its picture at once. A T1's items arrive flagged edition-unverified; a T0's arrive proven.
/// The session stays open and keeps scanning; every auto-add is undone on its own (<see cref="UndoAutoCommit"/>).
/// Writes nothing by itself; the caller saves, and deletes the collected cover files when the save fails.
/// </summary>
public sealed class AutoCommit(
    IAppDbContext db, IIdGenerator ids, TimeProvider clock, BarcodeLearner learner, ReleaseCovers covers)
{
    /// <summary>
    /// Adds the candidate's items when every gate allows it: tier T0 or T1, the session's tiers name that tier, the
    /// caller may add to the library, the session is still open, the person did not already reject it, and the
    /// candidate was not added already. Returns the created item ids, empty when the candidate stays for review.
    /// <paramref name="coverFiles"/> collects the cover files written, for the caller to delete when its save fails
    /// (nothing points at them then); null collects nothing. A caller adding from a whole picture passes its
    /// <paramref name="lookups"/>, so the releases are read once for all of the adds instead of once for each.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> TryAddAsync(
        ScanSession session, ScanCandidate candidate, Guid userId, LibraryRole? role, CancellationToken cancellationToken,
        ICollection<string>? coverFiles = null, AnswerLookups? lookups = null)
    {
        if (candidate.Tier is not (ScanTier.T0 or ScanTier.T1)
            || candidate.Tier is not { } tier
            || !session.AutoCommits(tier)
            || role is not (LibraryRole.Owner or LibraryRole.Editor)
            || session.Status != ScanSessionStatus.Open
            || candidate.Decision == CandidateDecision.Rejected
            || candidate.CreatedItemId is not null
            || candidate.MatchedReleaseId is null)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        var releaseFor = await new EditedReleases(db, ids).ResolveAsync(session, [candidate], now, cancellationToken, lookups);
        var releaseId = releaseFor.ForCandidate[candidate.Id];
        foreach (var written in await covers.FetchMissingAsync([releaseId], cancellationToken, lookups))
        {
            coverFiles?.Add(written);
        }

        var created = new List<Guid>();
        for (var copy = 0; copy < candidate.Copies; copy++)
        {
            var item = ScanCommitItems.Build(
                ids.NewId(),
                session.LibraryId,
                releaseId,
                session.DefaultStatus ?? ItemStatus.Owned,
                condition: null,
                ScanCommitItems.CompletenessOf(candidate),
                session.DefaultLocationId,
                CoverChoice.Release,
                photoId: null,
                ScanCommitItems.ViaOf(candidate.Source),
                userId,
                now,
                editionVerified: tier != ScanTier.T1);
            db.Items.Add(item);
            created.Add(item.Id);
            if (copy == 0)
            {
                candidate.CreatedItemId = item.Id;
            }
        }

        candidate.AutoCommittedItemIds = [.. created];
        session.AutoCommittedItemIds = [.. session.AutoCommittedItemIds ?? [], .. created];
        if (tier == ScanTier.T0)
        {
            // A barcode-proof add teaches its link; a cover match never teaches a barcode link, even when the row
            // happens to carry a code from an earlier bound capture.
            await learner.LearnAsync(session, [candidate], cancellationToken);
        }
        return created;
    }
}
