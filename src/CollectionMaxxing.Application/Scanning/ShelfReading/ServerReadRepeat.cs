using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.ShelfReading;

/// <summary>
/// A row the server's shelf read added while its release was still being looked up (Pending), and that the background job then answers:
/// when a phone row of the same snap already shows that release (and the server did not skip it), the server's row is that row read
/// again, as <c>:read-shelf</c> would have found had the answer come in time (<see cref="ReadShelfSnap"/>: a release a phone row has is not
/// added twice). It is then skipped as a repeat, so the release is never added twice. Otherwise it stays as the read left it: unconfirmed,
/// for the person to tick.
/// </summary>
internal static class ServerReadRepeat
{
    public static async Task<bool> IsRepeatAsync(IAppDbContext db, ScanCandidate row, CancellationToken cancellationToken)
    {
        if (row.Source != CandidateSource.PictureServerModel || row.SnapId is not { } snapId
            || CandidateJson.ReadAlternatives(row.AlternativesJson) is not { Count: > 0 } alternatives
            || PcShelfUpgrade.ReleaseKey.Of(alternatives[0].Title, alternatives[0].Year) is not { } key)
        {
            return false;
        }

        var phoneRows = await db.ScanCandidates.AsNoTracking()
            .Where(c => c.SessionId == row.SessionId && c.SnapId == snapId && c.Id != row.Id
                && (c.Source == CandidateSource.PictureText || c.Source == CandidateSource.PictureAppleModel))
            .Select(c => new { c.Decision, c.AlreadyOwnedCount, c.TraceJson, c.AlternativesJson })
            .ToListAsync(cancellationToken);
        return phoneRows.Any(c =>
            SkipReasons.Of(c.Decision, c.AlreadyOwnedCount, CandidateJson.ReadTrace(c.TraceJson)) is null
            && CandidateJson.ReadAlternatives(c.AlternativesJson) is { Count: > 0 } theirs
            && PcShelfUpgrade.ReleaseKey.Of(theirs[0].Title, theirs[0].Year) == key);
    }
}
