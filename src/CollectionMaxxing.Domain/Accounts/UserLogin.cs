namespace CollectionMaxxing.Domain.Accounts;

/// <summary>
/// One Auth0 identity linked to a user (docs/14, "Linking Auth0 identities to our users"). The <see cref="Subject"/> is Auth0's
/// <c>sub</c>, such as <c>google-oauth2|…</c> or <c>apple|…</c>, and it is unique: an identity belongs to one user at most.
/// People are never matched by email; <see cref="Email"/> is shown in Settings and nothing else.
/// </summary>
public sealed class UserLogin
{
    public const string Auth0Provider = "auth0";
    public const int MaxSubjectLength = 255;
    public const int MaxEmailLength = 320;

    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>Who vouches for the identity. Always <see cref="Auth0Provider"/> for now.</summary>
    public string Provider { get; init; } = Auth0Provider;

    public string Subject { get; init; } = string.Empty;

    /// <summary>The address the provider gave when the identity was linked. For display only.</summary>
    public string? Email { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the identity last signed in on the web.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
