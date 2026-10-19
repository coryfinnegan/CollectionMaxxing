namespace CollectionMaxxing.Domain.Accounts;

/// <summary>
/// One signed-in browser. The <c>__Host-cm</c> cookie carries this row's id; the server refuses the cookie as soon as the row is
/// revoked, so a copy of the cookie dies with the sign-out (docs/07, "Sign out, devices and deletion").
/// </summary>
public sealed class WebSession
{
    public const int MaxLabelLength = 100;

    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>What the person sees in Settings, for example "Edge on Windows". Made from a fixed vocabulary, never the raw User-Agent.</summary>
    public string Label { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the browser last used the session, to the minute (writes are throttled).</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Set when the session was signed out: by the person, "sign out everywhere", removal from the library or deleting the account.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
