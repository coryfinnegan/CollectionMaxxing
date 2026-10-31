namespace CollectionMaxxing.Api.Security;

/// <summary>
/// Puts an endpoint in one of the rate-limit groups of <see cref="SecurityOptions.LimitOptions"/>, besides the general allowance everything shares.
/// <see cref="ApiLimitsMiddleware"/> reads it from the endpoint the router matched, so the way a caller spells the path (letter case, a trailing
/// slash) can never move a request to another group.
/// </summary>
/// <param name="Name">The group, named like its property on <see cref="SecurityOptions.LimitOptions"/>.</param>
/// <param name="CostPerItem">True when a call costs one permit per item in its JSON body (<c>items</c>) instead of one permit.</param>
public sealed record RateLimitGroup(string Name, bool CostPerItem = false)
{
    public static RateLimitGroup Search { get; } = new(nameof(SecurityOptions.LimitOptions.Search));

    public static RateLimitGroup Barcodes { get; } = new(nameof(SecurityOptions.LimitOptions.Barcodes));

    public static RateLimitGroup Identify { get; } = new(nameof(SecurityOptions.LimitOptions.Identify), CostPerItem: true);

    public static RateLimitGroup Export { get; } = new(nameof(SecurityOptions.LimitOptions.Export));

    public static RateLimitGroup AuthHourly { get; } = new(nameof(SecurityOptions.LimitOptions.AuthHourly));
}

public static class RateLimitGroupExtensions
{
    /// <summary>Counts the endpoint (or every endpoint of a group) against <paramref name="group"/>.</summary>
    public static TBuilder WithRateLimitGroup<TBuilder>(this TBuilder builder, RateLimitGroup group)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(group);
}
