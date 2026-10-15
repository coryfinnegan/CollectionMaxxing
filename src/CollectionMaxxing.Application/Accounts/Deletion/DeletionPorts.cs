using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Accounts.Deletion;

/// <summary>Why deleting an account was refused.</summary>
public static class AccountDeletionErrors
{
    /// <summary>The user owns a library that other people belong to. Owners must transfer it or remove the members first.</summary>
    public static readonly DomainError LibraryHasMembers = new(
        ErrorKind.Conflict,
        "library_has_members",
        "Your library has other members, so it can't go with your account. Transfer ownership to someone else, or remove the members, and then delete your account.");
}

/// <summary>The audit actions of account deletion. Names are stable: they are stored.</summary>
public static class AccountAuditActions
{
    public const string Deleted = "account.deleted";
    public const string Purged = "account.purged";
}

/// <summary>
/// The accounts side of the purge, on ASP.NET Core Identity and OpenIddict (which Application never sees). Implemented by
/// Infrastructure. Every method can be repeated: a purge that stopped halfway runs them again.
/// </summary>
public interface IDeletedAccounts
{
    /// <summary>When the account was deleted, or null if it doesn't exist or is still active.</summary>
    Task<DateTimeOffset?> DeletedAtAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes what identifies the person from the audit trail: the address hash of their events, the id where they are the subject of an
    /// event, and any details that mention them. (The actor itself is cleared by <c>AnonymiseAuditActor</c>, which runs after this.)
    /// </summary>
    Task ScrubAuditTrailAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Erases the person: passkeys, roles, claims, paired devices and every token, remembered requests, email and name. The user row stays
    /// as a tombstone called "Removed user" because their items in other people's libraries still point at it (the "Added by" line).
    /// Also empties the payload of the purge job, so the queue no longer says whose account it was.
    /// </summary>
    Task EraseAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>The files of images that a purged library owned (covers it fetched, photos it kept).</summary>
public interface IAccountFiles
{
    /// <summary>Deletes the file at <paramref name="blobPath"/> (a path in the cover store). A file that is already gone, or a path that isn't safe, is fine.</summary>
    Task DeleteAsync(string blobPath, CancellationToken cancellationToken);
}

/// <summary>
/// Other features register one of these when they keep something for a library that the purge can't reach through the library's rows
/// (exports and their files, for instance). It runs for each library about to be purged, before the library's own rows go, and can run again.
/// </summary>
public interface ILibraryPurgeStep
{
    Task PurgeAsync(Guid libraryId, CancellationToken cancellationToken);
}

/// <summary>
/// Other features register one of these when they keep something for a person rather than for a library (an export file, say), so it
/// would outlive them if the purge only looked at libraries. It runs once for the user being purged, after their libraries, and can run again.
/// </summary>
public interface IUserPurgeStep
{
    Task PurgeAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Finds deleted accounts whose purge is overdue, so the daily sweep can queue it again (<see cref="PurgeSweep"/>).</summary>
public interface IPurgeBacklog
{
    /// <summary>
    /// Users deleted on or before <paramref name="deletedBefore"/> that still hold something a purge removes: a place in a library, a passkey,
    /// or a name other than the tombstone's. A purged user holds none of these, so they are not listed.
    /// </summary>
    Task<IReadOnlyList<Guid>> UsersAwaitingPurgeAsync(DateTimeOffset deletedBefore, CancellationToken cancellationToken);
}

/// <summary>Marks the account deleted, but only if that leaves no library behind without an owner.</summary>
public interface IAccountDeactivation
{
    /// <summary>
    /// Deactivates the account (it can't sign in; every session is refused from the next request) unless it owns a library that other people
    /// belong to. The check and the change are one step that also holds off an invite being accepted at the same moment, so a member
    /// cannot slip in between them. Returns false, changing nothing, when the library has members. Doing it twice is fine.
    /// </summary>
    Task<bool> DeactivateUnlessLibraryHasMembersAsync(Guid userId, CancellationToken cancellationToken);
}
