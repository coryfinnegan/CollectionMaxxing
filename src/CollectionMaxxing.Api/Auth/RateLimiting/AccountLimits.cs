using System.Globalization;
using System.Threading.RateLimiting;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// The onboarding limits of docs/15 ("Security"), on top of the per-client <see cref="RateLimiting.PolicyName"/> policy: creating a library
/// and previewing or accepting an invite are limited per account too (<c>Auth:RateLimit:AccountPermitLimit</c> a window), and new accounts
/// per client (<c>Auth:RateLimit:NewAccountsPerHour</c>). The ASP.NET Core limiter runs before sign-in is known, so the per-account one is an
/// endpoint filter. Idle buckets are forgotten.
/// </summary>
public sealed class AccountLimits : IDisposable
{
    private readonly PartitionedRateLimiter<string> _perAccount;
    private readonly PartitionedRateLimiter<string> _newAccounts;

    public AccountLimits(IOptions<AuthOptions> options)
    {
        var settings = options.Value.RateLimit;
        _perAccount = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, settings.AccountPermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, settings.WindowSeconds)),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
        _newAccounts = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, settings.NewAccountsPerHour),
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
    }

    /// <summary>Takes one permit from the account's bucket; null when allowed, otherwise how long to wait.</summary>
    public TimeSpan? TryTakeForAccount(Guid userId) => TryTake(_perAccount, userId.ToString("N", CultureInfo.InvariantCulture));

    /// <summary>Takes one permit from the client's "new accounts" bucket; null when allowed, otherwise how long to wait.</summary>
    public TimeSpan? TryTakeNewAccount(string client) => TryTake(_newAccounts, client);

    public void Dispose()
    {
        _perAccount.Dispose();
        _newAccounts.Dispose();
    }

    /// <summary>429 as Problem Details with <c>Retry-After</c>, the same answer the per-client limiter gives.</summary>
    public static IResult Refused(HttpContext context, TimeSpan wait)
    {
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return AuthProblems.TooManyAttempts();
    }

    private static TimeSpan? TryTake(PartitionedRateLimiter<string> limiter, string key)
    {
        using var lease = limiter.AttemptAcquire(key);
        if (lease.IsAcquired)
        {
            return null;
        }

        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromMinutes(1);
    }
}

/// <summary>Refuses a signed-in caller who has used up their account's allowance (<see cref="AccountLimits"/>).</summary>
public sealed class AccountRateLimitFilter(AccountLimits limits) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (CurrentSession.UserId(context.HttpContext.User) is { } userId && limits.TryTakeForAccount(userId) is { } wait)
        {
            return AccountLimits.Refused(context.HttpContext, wait);
        }

        return await next(context);
    }
}

public static class AccountRateLimitExtensions
{
    /// <summary>Limited per client (the <see cref="RateLimiting.PolicyName"/> policy) and per account (<see cref="AccountRateLimitFilter"/>).</summary>
    public static TBuilder RequirePerClientAndAccountRateLimit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRateLimiting(RateLimiting.PolicyName).AddEndpointFilter<TBuilder, AccountRateLimitFilter>();
}
