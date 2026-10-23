using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Libraries.Members;

/// <summary>
/// The people behind user ids. Users belong to ASP.NET Core Identity, which Application never sees, so names and the state of an account
/// come through this port (implemented by Infrastructure).
/// </summary>
public interface IUserDirectory
{
    /// <summary>The display name of each user that exists, by id. A deleted account keeps its name until it is purged.</summary>
    Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>The account's own display name and email, or null when there is no such user.</summary>
    Task<AccountProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Whether the account was deleted. A user who doesn't exist at all is not "deactivated".</summary>
    Task<bool> IsDeactivatedAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// The accounts (not deleted) whose own email, or the email of one of their sign-ins, is <paramref name="email"/>, ignoring case. For the
    /// host's recovery tool only: nobody is ever signed in or linked by email.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindActiveByEmailAsync(string email, CancellationToken cancellationToken);
}

/// <summary>What an account says about itself: its display name, and its email (null when the sign-in gave none).</summary>
public sealed record AccountProfile(string DisplayName, string? Email);

/// <summary>Why a member request was refused.</summary>
public static class MemberErrors
{
    /// <summary>The user isn't a member of this library (or doesn't exist). The two look the same.</summary>
    public static readonly DomainError NotFound =
        new(ErrorKind.NotFound, "member.not_found", "That person isn't a member of this library.");

    public static readonly DomainError OwnerIsFixed =
        new(ErrorKind.Conflict, "member.owner", "The owner can't be removed or given another role. A library always has its owner.");

    /// <summary>The owner tried to leave their own library. A library always has its owner.</summary>
    public static readonly DomainError LastOwner =
        new(ErrorKind.Conflict, "library.last_owner", "You own this library, so you can't leave it.");

    public static readonly DomainError RoleRequired =
        new(ErrorKind.Validation, "member.role_required", "Choose a role: editor or viewer.", "role");

    public static readonly DomainError RoleNotAssignable =
        new(ErrorKind.Validation, "member.role_invalid", "A member can be an editor or a viewer. Ownership is transferred separately.", "role");
}
