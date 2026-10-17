using System.Security.Cryptography;
using System.Text;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

public static class ScanningErrors
{
    /// <summary>
    /// The session doesn't exist, belongs to another library, or was started by someone else. The three look the same on
    /// purpose: a session is its user's own.
    /// </summary>
    public static readonly DomainError SessionNotFound =
        new(ErrorKind.NotFound, "scan.session_not_found", "That scan session doesn't exist.");

    public static readonly DomainError CandidateNotFound =
        new(ErrorKind.NotFound, "scan.candidate_not_found", "That scanned item doesn't exist in this session.");

    public static readonly DomainError SessionClosed =
        new(ErrorKind.Conflict, "scan.session_closed", "This scan session is already finished. Start a new one to scan more.");

    public static readonly DomainError AlreadyCommitted =
        new(ErrorKind.Conflict, "scan.already_committed", "This scan session was already added to the library.");

    public static readonly DomainError ReleaseUnavailable = new(
        ErrorKind.Conflict,
        "scan.release_unavailable",
        "One of the accepted items can't be added any more, so nothing was added. Review the session and try again.");

    public static readonly DomainError NotCommitted = new(
        ErrorKind.Conflict,
        "scan.not_committed",
        "This scan session hasn't been added to the library, so there is nothing to undo.");

    public static readonly DomainError UndoWindowPassed = new(
        ErrorKind.Conflict,
        "scan.undo_window_passed",
        "It's too late to undo this. The items stay in your library; you can remove them from there.");

    public static readonly DomainError UndoItemsChanged = new(
        ErrorKind.Conflict,
        "scan.undo_items_changed",
        "Some of the new items have changed since they were added, so they were left as they are.");

    public static DomainError Invalid(string code, string message, string field) => new(ErrorKind.Validation, code, message, field);
}

/// <summary>Finds the caller's own scan session.</summary>
internal static class ScanSessions
{
    /// <summary>
    /// The key a request is remembered under: the caller's <c>Idempotency-Key</c> scoped to this session and purpose, and short
    /// enough for the table's 200-character column however long the caller's key is.
    /// </summary>
    public static string IdempotencyKeyFor(string purpose, Guid sessionId, string key) =>
        $"scan-{purpose}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}|{key}")))}";

    /// <summary>The session, tracked for update, when it exists in a library the caller can see and the caller started it.</summary>
    public static async Task<ScanSession?> FindMineAsync(IAppDbContext db, ICurrentUser currentUser, Guid sessionId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return null;
        }

        return await db.ScanSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);
    }
}
