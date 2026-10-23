using System.Threading.RateLimiting;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Limits how fast anyone can try codes and start sign-ins: the endpoints that take a recovery code or an invite, start a sign-in or create
/// a library are the ones a stranger could hammer. One bucket per client address. 429 with <c>Retry-After</c>, as Problem Details. The
/// per-account and new-account limits are <see cref="AccountLimits"/>.
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
        services.AddSingleton<AccountLimits>();
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
