using Microsoft.AspNetCore.Identity;

namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>
/// A person who can sign in. Passwordless: <c>password_hash</c> is never filled in, sign-in is with an Auth0 identity linked in
/// <c>user_logins</c>. <c>Email</c> is optional and never used to sign in.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public const int MaxDisplayNameLength = 100;

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Set when the account is deleted: they can't sign in, and their data is purged within 30 days.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Access tokens issued before this moment are refused (set when the phones are signed out, a member is removed or the account is deleted).
    /// Compared in whole seconds with the token's <c>iat</c>; see <c>ITokenCutoffs</c>.
    /// </summary>
    public DateTimeOffset? TokensNotBefore { get; set; }
}
