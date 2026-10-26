using CollectionMaxxing.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Provisional;

/// <summary>
/// Keeps kept rows' links honest when the rows they cite go away (design §11). The links are Restrict foreign keys —
/// nulling a composite would null <c>library_id</c> too, which must never be null — so whoever deletes sessions, snaps,
/// candidates or provisionals clears the citations first. Saves nothing; the caller saves.
/// </summary>
internal static class ProvisionalLinks
{
    /// <summary>Clears the session, snap and candidate links of a session's kept rows (the 7-day cleanup deletes all three).</summary>
    public static async Task ClearSessionLinksAsync(IAppDbContext db, List<Guid> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        await db.ProvisionalRecords.IgnoreQueryFilters()
            .Where(p => p.SessionId != null && sessionIds.Contains(p.SessionId.Value))
            .ExecuteUpdateAsync(
                set => set.SetProperty(p => p.SessionId, (Guid?)null).SetProperty(p => p.SnapId, (Guid?)null).SetProperty(p => p.CandidateId, (Guid?)null),
                cancellationToken);
    }

    /// <summary>Clears the snap link of kept rows that cite a deleted photo snap (a replaced, committed or abandoned "Use my photo").</summary>
    public static async Task ClearSnapLinksAsync(IAppDbContext db, List<Guid> snapIds, CancellationToken cancellationToken)
    {
        if (snapIds.Count == 0)
        {
            return;
        }

        await db.ProvisionalRecords.IgnoreQueryFilters()
            .Where(p => p.SnapId != null && snapIds.Contains(p.SnapId.Value))
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.SnapId, (Guid?)null), cancellationToken);
    }

    /// <summary>Clears the kept-record link of candidates whose provisional record is being deleted (DeleteProvisional).</summary>
    public static async Task ClearKeptLinksAsync(IAppDbContext db, Guid provisionalId, CancellationToken cancellationToken)
    {
        await db.ScanCandidates.IgnoreQueryFilters()
            .Where(c => c.ProvisionalId == provisionalId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ProvisionalId, (Guid?)null), cancellationToken);
    }

    /// <summary>Clears every kept-record link of a library's candidates (the account purge deletes provisionals before candidates).</summary>
    public static async Task ClearKeptLinksForLibraryAsync(IAppDbContext db, Guid libraryId, CancellationToken cancellationToken)
    {
        await db.ScanCandidates.IgnoreQueryFilters()
            .Where(c => c.LibraryId == libraryId && c.ProvisionalId != null)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ProvisionalId, (Guid?)null), cancellationToken);
    }
}
