using System.Threading.RateLimiting;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Limits how fast anyone can try codes and passkeys: the endpoints that take a setup, recovery or invite code, or a passkey,
/// are the ones a stranger on the tailnet could hammer. 429 with <c>Retry-After</c>, as Problem Details.
/// </summary>
public static class RateLimiting
{
    public const string PolicyName = "auth";

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<TrustedProxyOptions>()
            .BindConfiguration(TrustedProxyOptions.SectionName)
            .Validate(ClientAddressResolver.EntriesAreValid, "Auth:RateLimit:TrustedProxies must list IP addresses or ranges (for example 127.0.0.1 or 172.16.0.0/12).")
            .ValidateOnStart();
        services.AddSingleton<ClientAddressResolver>();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AuthOptions>>((limiter, auth) =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(PolicyName, context =>
            {
                var settings = auth.Value.RateLimit;

                // One bucket per caller address. Behind Tailscale Serve that is the forwarded client address, but only when the
                // connection comes from a configured trusted proxy (Auth:RateLimit:TrustedProxies); otherwise it is the connection's
                // own address, so a made-up X-Forwarded-For changes nothing. With no proxy configured, everyone behind it shares
                // one bucket: safe, if cramped.
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.RequestServices.GetRequiredService<ClientAddressResolver>().Resolve(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PermitLimit,
                        Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                        QueueLimit = 0,
                    });
            });
            limiter.OnRejected = async (rejected, cancellationToken) =>
            {
                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    rejected.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await AuthProblems.TooManyAttempts().ExecuteAsync(rejected.HttpContext);
            };
        });
        return services;
    }
}
