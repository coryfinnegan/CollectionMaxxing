using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Security;

/// <summary>
/// The "Rate limits" table of docs/08 (apart from the 10-a-minute code and sign-in limiter, which is <c>Auth:RateLimit</c>). Each caller has
/// one bucket per group: a signed-in user is a user, anyone else is the address they came from. Idle buckets are forgotten.
/// </summary>
public sealed class ApiLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<(string Group, string Key)> _limiter;

    public ApiLimiter(IOptions<SecurityOptions> options)
    {
        var limits = options.Value.Limits;
        _limiter = PartitionedRateLimiter.Create<(string Group, string Key), string>(request =>
        {
            var (group, key) = request;
            var partition = group + "|" + key;
            var limit = Of(limits, group);
            return group == nameof(SecurityOptions.LimitOptions.Default)
                ? RateLimitPartition.GetTokenBucketLimiter(partition, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limit.Permits,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(Math.Max(0.01, (double)limit.WindowSeconds / limit.Permits)),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })
                : RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit.Permits,
                    Window = TimeSpan.FromSeconds(limit.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        });
        Options = limits;
    }

    private SecurityOptions.LimitOptions Options { get; }

    /// <summary>Takes <paramref name="permits"/> from the caller's bucket; null when allowed, otherwise how long to wait.</summary>
    public TimeSpan? TryTake(string group, string key, int permits)
    {
        var limit = Of(Options, group);
        if (permits > limit.Permits)
        {
            return TimeSpan.FromSeconds(limit.WindowSeconds); // more than the whole allowance: never allowed in one go
        }

        using var lease = _limiter.AttemptAcquire((group, key), permits);
        if (lease.IsAcquired)
        {
            return null;
        }

        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromSeconds(limit.WindowSeconds);
    }

    public void Dispose() => _limiter.Dispose();

    private static SecurityOptions.GroupLimit Of(SecurityOptions.LimitOptions limits, string group) => group switch
    {
        nameof(SecurityOptions.LimitOptions.Search) => limits.Search,
        nameof(SecurityOptions.LimitOptions.Barcodes) => limits.Barcodes,
        nameof(SecurityOptions.LimitOptions.Identify) => limits.Identify,
        nameof(SecurityOptions.LimitOptions.Export) => limits.Export,
        nameof(SecurityOptions.LimitOptions.AuthHourly) => limits.AuthHourly,
        _ => limits.Default,
    };
}

/// <summary>Decides which groups a request belongs to, takes its permits and answers 429 (Problem Details, with Retry-After) when one is empty.</summary>
public sealed class ApiLimitsMiddleware(RequestDelegate next, ApiLimiter limiter, ClientAddressResolver addresses)
{
    private const int MaxBodyToRead = 1024 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        var key = CallerKey(context);
        // The specific group first, so a refused call does not also use up the caller's general allowance.
        var (group, permits) = await SpecificAsync(context.Request, path);
        if (group is not null)
        {
            var wait = limiter.TryTake(group, key, permits == 0 ? await CountItemsAsync(context.Request) : permits);
            if (wait is { } refused)
            {
                await Refuse(context, refused);
                return;
            }
        }

        if (limiter.TryTake(nameof(SecurityOptions.LimitOptions.Default), key, 1) is { } waitDefault)
        {
            await Refuse(context, waitDefault);
            return;
        }

        await next(context);
    }

    /// <summary>The group a request counts against besides "everything else", and the permits it costs (0: one per item in the body).</summary>
    private static async Task<(string? Group, int Permits)> SpecificAsync(HttpRequest request, PathString path)
    {
        var method = request.Method;
        var value = path.Value ?? string.Empty;
        if (HttpMethods.IsGet(method) && value.TrimEnd('/') == "/api/v1/me/export")
        {
            return (nameof(SecurityOptions.LimitOptions.Export), 1);
        }

        if (HttpMethods.IsGet(method) && (value.TrimEnd('/') == "/api/v1/catalog/search" || value.TrimEnd('/') == "/api/v1/catalog/lookup"))
        {
            return (nameof(SecurityOptions.LimitOptions.Search), 1);
        }

        if (HttpMethods.IsPost(method) && value.StartsWith("/api/v1/scan-sessions/", StringComparison.Ordinal))
        {
            if (value.EndsWith("/barcodes", StringComparison.Ordinal))
            {
                return (nameof(SecurityOptions.LimitOptions.Barcodes), 1);
            }

            if (value.EndsWith("/identify", StringComparison.Ordinal))
            {
                return (nameof(SecurityOptions.LimitOptions.Identify), 0);
            }
        }

        // Signing out must always work, whoever has been busy from the same address.
        if (HttpMethods.IsPost(method) && path.StartsWithSegments("/account/logout"))
        {
            return (null, 0);
        }

        if (HttpMethods.IsPost(method) && path.StartsWithSegments("/account"))
        {
            return (nameof(SecurityOptions.LimitOptions.AuthHourly), 1);
        }

        return (null, 0);
    }

    /// <summary>A signed-in caller is their user id; anyone else is the address they came from.</summary>
    private string CallerKey(HttpContext context) =>
        CurrentSession.UserId(context.User) is { } userId
            ? "u:" + userId.ToString("N", CultureInfo.InvariantCulture)
            : "ip:" + addresses.Resolve(context);

    /// <summary>How many items an identify call carries: the length of the body's <c>items</c> array, at least 1. A body we can't read costs 1; the endpoint judges it.</summary>
    private static async Task<int> CountItemsAsync(HttpRequest request)
    {
        if (request.ContentLength is > MaxBodyToRead)
        {
            return 1;
        }

        try
        {
            request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: MaxBodyToRead);
            using var document = await JsonDocument.ParseAsync(request.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                // The endpoint reads property names without regard to case, so the count must too.
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "items", StringComparison.OrdinalIgnoreCase))
                    {
                        return property.Value.ValueKind == JsonValueKind.Array ? Math.Max(1, property.Value.GetArrayLength()) : 1;
                    }
                }
            }

            return 1;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return 1;
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private static async Task Refuse(HttpContext context, TimeSpan wait)
    {
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        await ProblemResults.Problem(
            StatusCodes.Status429TooManyRequests, "Too many requests", "Slow down a little and try again shortly.", "rate_limited")
            .ExecuteAsync(context);
    }
}
