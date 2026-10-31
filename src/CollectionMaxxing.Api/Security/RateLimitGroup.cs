namespace CollectionMaxxing.Api.Security;

/// <summary>
/// Puts an endpoint in one of the rate-limit groups of <see cref="SecurityOptions.LimitOptions"/>, besides the general allowance everything shares.
/// <see cref="ApiLimitsMiddleware"/> reads it from the endpoint the router matched, so the way a caller spells the path (letter case, a trailing
/// slash) can never move a request to another group.
/// </summary>
/// <param name="Name">The group, named like its property on <see cref="SecurityOptions.LimitOptions"/>.</param>
/// <param name="Cost">
/// How many permits a call costs, for a group whose cost depends on the call (identify costs one per item its body carries); one
/// permit when null. It runs before the endpoint, so it must read nothing the endpoint cannot read again. Zero takes nothing from
/// the group (the call still counts against the caller's general allowance).
/// </param>
public sealed record RateLimitGroup(string Name, Func<HttpContext, ValueTask<int>>? Cost = null)
{
    public static RateLimitGroup Search { get; } = new(nameof(SecurityOptions.LimitOptions.Search));

    public static RateLimitGroup Barcodes { get; } = new(nameof(SecurityOptions.LimitOptions.Barcodes));

    public static RateLimitGroup Export { get; } = new(nameof(SecurityOptions.LimitOptions.Export));

    public static RateLimitGroup AuthHourly { get; } = new(nameof(SecurityOptions.LimitOptions.AuthHourly));
}

public static class RateLimitGroupExtensions
{
    /// <summary>Counts the endpoint (or every endpoint of a group) against <paramref name="group"/>.</summary>
    public static TBuilder WithRateLimitGroup<TBuilder>(this TBuilder builder, RateLimitGroup group)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(group);
}
