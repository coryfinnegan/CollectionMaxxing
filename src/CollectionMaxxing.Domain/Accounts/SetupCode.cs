namespace CollectionMaxxing.Domain.Accounts;

public enum SetupCodePurpose
{
    /// <summary>Creates the owner. Works only while no owner exists.</summary>
    Setup,

    /// <summary>Lets someone who lost every passkey register a new one. Comes from the PC's command line.</summary>
    Recovery,
}

/// <summary>A one-time code printed on the server: first-run setup, or recovery after losing every passkey.</summary>
public sealed class SetupCode
{
    public Guid Id { get; init; }

    public SetupCodePurpose Purpose { get; init; }

    public string CodeHash { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }
}
