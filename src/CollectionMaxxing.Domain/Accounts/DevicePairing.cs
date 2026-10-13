namespace CollectionMaxxing.Domain.Accounts;

/// <summary>A short-lived, single-use pairing for one iPhone. The plaintext token only ever appears in the QR code.</summary>
public sealed class DevicePairing
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public string TokenHash { get; init; } = string.Empty;

    /// <summary>Hash of the 6-digit fallback code, for when the camera can't read the QR code.</summary>
    public string CodeHash { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Two minutes after creation.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Wrong guesses of the 6-digit code; five locks the pairing.</summary>
    public int Attempts { get; set; }

    public string? DeviceName { get; set; }

    /// <summary>The OpenIddict authorization created when the pairing was redeemed.</summary>
    public Guid? AuthorizationId { get; set; }
}
