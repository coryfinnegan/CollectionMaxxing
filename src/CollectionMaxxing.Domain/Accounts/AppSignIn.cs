namespace CollectionMaxxing.Domain.Accounts;

/// <summary>
/// One iPhone that signed in with Auth0 and told the server about itself (docs/14, "Devices and revocation"). The app picks an install id
/// once and sends it on every registration, so signing in again on the same phone updates this row instead of adding another.
/// Auth0 keeps the tokens; this row only lets Settings list the phone with its last-seen time.
/// </summary>
public sealed class AppSignIn
{
    public const int MaxDeviceNameLength = 100;

    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>The app's own id for this install (the <c>X-Install-Id</c> header). Unique per user.</summary>
    public Guid InstallId { get; init; }

    public string DeviceName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }
}
