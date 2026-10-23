namespace CollectionMaxxing.Domain.Accounts;

public enum SetupCodePurpose
{
    /// <summary>Lets someone who lost every sign-in method link a new one to their account. Comes from the PC's command line.</summary>
    Recovery = 1,
}

/// <summary>
/// A one-time recovery code printed on the server (<c>cm-admin recovery-code &lt;email&gt;</c>) for one named account. Only its hash is
/// stored. (The table keeps its old name, <c>setup_codes</c>; there is no setup code any more, ADR-027.)
/// </summary>
public sealed class SetupCode
{
    public Guid Id { get; init; }

    public SetupCodePurpose Purpose { get; init; }

    /// <summary>The account the code recovers. The new sign-in it links goes to this account and no other.</summary>
    public Guid UserId { get; init; }

    public string CodeHash { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }
}
