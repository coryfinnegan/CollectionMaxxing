using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Invites;

/// <summary>
/// How invite tokens and codes look, how they are stored (a hash only) and how long they last (docs/07 "Family members", docs/15
/// "Invite codes").
/// </summary>
public static class InviteSecrets
{
    /// <summary>An invite is good for seven days from the moment the owner makes it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Crockford's base32: no I, L, O or U, so a code read off a screen is hard to mistype.</summary>
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A code is 10 characters (50 bits), shown as <c>ABCD-EFGH-JK</c>.</summary>
    public const int CodeLength = 10;

    /// <summary>A random token for the link: 256 bits, URL-safe text.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>A random short code, formatted <c>ABCD-EFGH-JK</c>.</summary>
    public static string NewCode()
    {
        var code = RandomNumberGenerator.GetString(Crockford, CodeLength);
        return $"{code[..4]}-{code[4..8]}-{code[8..]}";
    }

    /// <summary>
    /// The code as stored before hashing, or null when <paramref name="typed"/> can't be a code. Typing is forgiving: case, dashes and spaces
    /// don't matter, and O counts as 0 and I and L as 1, as Crockford's base32 allows.
    /// </summary>
    public static string? NormalizeCode(string typed)
    {
        var normalized = new StringBuilder(CodeLength);
        foreach (var c in typed)
        {
            if (c is '-' or ' ' or '\t')
            {
                continue;
            }

            var upper = char.ToUpperInvariant(c) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (!Crockford.Contains(upper, StringComparison.Ordinal) || normalized.Length == CodeLength)
            {
                return null;
            }

            normalized.Append(upper);
        }

        return normalized.Length == CodeLength ? normalized.ToString() : null;
    }

    /// <summary>
    /// What the database stores for a token. The token is random and long, so a plain SHA-256 is enough; the point is that a copy of the
    /// database can't be used to join the library.
    /// </summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>What the database stores for a code: the hash of its normalized form, so however it is typed it finds the same row.</summary>
    public static string HashCode(string normalizedCode) => Hash("code:" + normalizedCode);
}

/// <summary>
/// What a person pasted or typed to join: an invite link (<c>https://…/join/{token}</c>), a link's token, or a short code. Only the hash it
/// is looked up by is kept.
/// </summary>
public sealed record InviteKey(string Hash, bool IsCode)
{
    /// <summary>Reads <paramref name="invite"/>. Anything at all gives a key (blank text included): what it can't be is simply not found.</summary>
    public static InviteKey Parse(string? invite)
    {
        var text = invite?.Trim() ?? string.Empty;
        var join = text.LastIndexOf("/join/", StringComparison.OrdinalIgnoreCase);
        if (join >= 0)
        {
            var token = text[(join + "/join/".Length)..];
            var end = token.IndexOfAny(['?', '#', '/']);
            return new InviteKey(InviteSecrets.Hash(end >= 0 ? token[..end] : token), false);
        }

        return InviteSecrets.NormalizeCode(text) is { } code
            ? new InviteKey(InviteSecrets.HashCode(code), true)
            : new InviteKey(InviteSecrets.Hash(text), false);
    }

    /// <summary>A link's token, as <c>GET /api/v1/invites/{token}</c> has it.</summary>
    public static InviteKey ForToken(string? token) => new(InviteSecrets.Hash(token ?? string.Empty), false);
}

/// <summary>Why an invite request was refused.</summary>
public static class InviteErrors
{
    /// <summary>
    /// The invite is unknown, expired, used or revoked: all four look exactly the same, so nobody can tell whether a token or code ever
    /// existed.
    /// </summary>
    public static readonly DomainError Invalid =
        new(ErrorKind.NotFound, "invite.invalid", "This invite isn't valid any more. Ask for a new one.");

    /// <summary>The caller is a member of the invite's library already. The invite isn't spent.</summary>
    public static readonly DomainError AlreadyMember =
        new(ErrorKind.Conflict, "invite.already_member", "You're already a member of this library.");

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
