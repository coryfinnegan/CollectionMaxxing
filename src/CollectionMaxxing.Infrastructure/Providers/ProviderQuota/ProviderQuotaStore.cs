using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.Providers.Quota;

/// <summary>
/// The daily request allowance in <c>provider_quota</c> (UPCitemdb's 100 a day). The counter lives in the database, so it
/// survives restarts, and the day is the UTC day, so it resets at midnight UTC.
/// </summary>
public sealed class ProviderQuotaStore(IServiceScopeFactory scopes, TimeProvider time)
{
    /// <summary>
    /// Spends one request of today's allowance in a single atomic statement. Returns false, spending nothing, when the
    /// day's <paramref name="limit"/> is already used.
    /// </summary>
    public async Task<bool> TryConsumeAsync(string provider, int limit, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        // New day: insert with one used. Existing day: add one, but only while under the limit.
        var changed = await db.Database.ExecuteSqlAsync(
            $"""
            insert into provider_quota (provider, day, used, "limit")
            select {provider}, {today}, 1, {limit} where {limit} > 0
            on conflict (provider, day) do update
              set used = provider_quota.used + 1, "limit" = excluded."limit"
              where provider_quota.used < excluded."limit"
            """,
            ct);
        return changed > 0;
    }
}
