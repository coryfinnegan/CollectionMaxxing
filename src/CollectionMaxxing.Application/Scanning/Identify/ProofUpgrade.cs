using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// Completes an edition proof (docs/16 §11): the read's answer is in, and the bound item is upgraded when it is a match
/// on the item's own work — the release link swaps in place, the edition verifies, and the row becomes the item (linked,
/// never a second copy). Anything else (another work, no match, still waiting) leaves the item alone; the caller keeps
/// the row for review. Shared by the request and its background job, so both finish a proof the same way. Saves nothing;
/// the caller saves.
/// </summary>
internal static class ProofUpgrade
{
    /// <summary>
    /// Upgrades the bound item from the candidate's answer. True when the item was upgraded. <paramref name="role"/> is the
    /// sender's role in the library at this moment (<see cref="EditionProofAccess"/>): a viewer's proof never changes an item,
    /// whichever path finished it.
    /// </summary>
    public static async Task<bool> TryApplyAsync(
        IAppDbContext db, ScanSession session, ScanCandidate candidate, Guid itemId, LibraryRole? role, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!EditionProofAccess.Allows(role)
            || candidate is not { Resolution: CandidateResolution.Matched, MatchedReleaseId: { } releaseId })
        {
            return false;
        }

        var item = await db.Items.FirstOrDefaultAsync(
            i => i.LibraryId == session.LibraryId && i.Id == itemId && i.DeletedAt == null, cancellationToken);
        if (item is null)
        {
            return false;
        }

        // Through the tracker: a release the answer just ensured exists only in this unit of work until the caller
        // saves. A private release of another library is never attached; the row stays for review instead.
        var next = await ItemProjection.LoadReleaseWithWorkAsync(db, releaseId, cancellationToken);
        if (next is null || (next.Release.OwnerLibraryId is not null && next.Release.OwnerLibraryId != session.LibraryId))
        {
            return false;
        }

        var current = await ItemProjection.LoadReleaseWithWorkAsync(db, item.ReleaseId, cancellationToken);
        if (current is null || current.Work.Id != next.Work.Id)
        {
            return false;
        }

        item.ReleaseId = releaseId;
        item.EditionVerified = true;
        item.UpdatedAt = clock.GetUtcNow();
        candidate.CreatedItemId = item.Id;
        return true;
    }
}
