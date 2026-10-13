namespace CollectionMaxxing.Domain.Catalog;

/// <summary>A cached answer from an outside catalogue provider. Re-fetched on demand once it expires.</summary>
public sealed class ProviderCacheEntry
{
    public string Provider { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";

    public DateTimeOffset FetchedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>How much of a provider's daily request allowance has been used (UPCitemdb's daily cap).</summary>
public sealed class ProviderQuota
{
    public string Provider { get; init; } = string.Empty;

    public DateOnly Day { get; init; }

    public int Used { get; set; }

    public int Limit { get; set; }
}
