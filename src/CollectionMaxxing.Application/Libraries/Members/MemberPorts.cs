using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Libraries.Members;

/// <summary>
/// The people behind user ids. Users belong to ASP.NET Core Identity, which Application never sees, so names and the
/// deactivation of an account come through this port (implemented by Infrastructure).
/// </summary>
public interface IUserDirectory
{
    /// <summary>The display name of each user that exists, by id. A deactivated account keeps its name (their items still say who added them).</summary>
    Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>
    /// Deactivates the account: it can't sign in and every session it has is refused from the next request. The user row and its name stay.
    /// Doing it twice is fine.
    /// </summary>
    Task DeactivateAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Whether the account was deactivated (removed from its library, or deleted). A user who doesn't exist at all is not "deactivated".</summary>
    Task<bool> IsDeactivatedAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Why a member request was refused.</summary>
public static class MemberErrors
{
    /// <summary>The user isn't a member of this library (or doesn't exist). The two look the same.</summary>
    public static readonly DomainError NotFound =
        new(ErrorKind.NotFound, "member.not_found", "That person isn't a member of this library.");

    public static readonly DomainError OwnerIsFixed =
        new(ErrorKind.Conflict, "member.owner", "The owner can't be removed or given another role. A library always has its owner.");

    public static readonly DomainError RoleRequired =
        new(ErrorKind.Validation, "member.role_required", "Choose a role: editor or viewer.", "role");

    public static readonly DomainError RoleNotAssignable =
        new(ErrorKind.Validation, "member.role_invalid", "A member can be an editor or a viewer. Ownership is transferred separately.", "role");
}
