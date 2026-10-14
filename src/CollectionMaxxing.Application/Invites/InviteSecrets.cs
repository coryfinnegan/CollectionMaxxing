using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Invites;

/// <summary>How invite tokens look, how they are stored (a hash only) and how long they last (docs/07, "Family members").</summary>
public static class InviteSecrets
{
    /// <summary>An invite is good for seven days from the moment the owner makes it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>A random token for the link: 256 bits, URL-safe text.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// What the database stores. The token is random and long, so a plain SHA-256 is enough; the point is that a copy of the
    /// database can't be used to join the library.
    /// </summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>Why an invite request was refused.</summary>
public static class InviteErrors
{
    /// <summary>
    /// The token is unknown, expired, used or revoked: all four look exactly the same, so nobody can tell whether a token ever
    /// existed.
    /// </summary>
    public static readonly DomainError Invalid =
        new(ErrorKind.NotFound, "invite.invalid", "This invite isn't valid any more. Ask for a new one.");

    public static readonly DomainError NameRequired =
        new(ErrorKind.Validation, "invite.name_required", "Enter a name for the person you are inviting.", "name");

    public static readonly DomainError NameTooLong =
        new(ErrorKind.Validation, "invite.name_too_long", "That name is too long.", "name");

    public static readonly DomainError RoleRequired =
        new(ErrorKind.Validation, "invite.role_required", "Choose a role: editor or viewer.", "role");

    public static readonly DomainError RoleNotInvitable =
        new(ErrorKind.Validation, "invite.role_invalid", "An invite can be for an editor or a viewer. A library has one owner.", "role");

    public static readonly DomainError NotFound =
        new(ErrorKind.NotFound, "invite.not_found", "That invite doesn't exist.");

    public static readonly DomainError AlreadyAccepted =
        new(ErrorKind.Conflict, "invite.accepted", "That invite has already been used. Remove the member instead.");
}
