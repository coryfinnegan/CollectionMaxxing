using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.Infrastructure.Providers.Caching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Infrastructure.Jobs;

public sealed class CleanExpiredRowsPayload;

/// <summary>
/// Recurring job: three tables collect rows that stop mattering at a known time, and nothing else removes them, so they
/// would only ever grow. This deletes them once a day:
/// <list type="bullet">
/// <item><c>idempotency_keys</c>: replay keys past their expiry. An expired key is already ignored by every reader.</item>
/// <item>
/// <c>provider_cache</c>: cached provider answers and search hits past their expiry, except the permanent ISBN copy
/// (<see cref="ProviderCacheIsbnStore.StoreName"/>), which is kept on purpose and never removed.
/// </item>
/// <item><c>setup_codes</c>: recovery codes that were used or have expired. A code that is unused and in date stays.</item>
/// </list>
/// Rows go in batches, oldest first, so one run never holds a long lock; rows another worker holds locked are left for the
/// next run. A run gives up on a table after <see cref="MaxBatchesPerTable"/> batches, and the rest goes the next day.
/// </summary>
public static class CleanExpiredRows
{
    public const string JobType = "cleanup.expired-rows";

    public const int BatchSize = 1000;

    private const int MaxBatchesPerTable = 200;

    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    public sealed class Cleaner(AppDbContext db, TimeProvider clock, ILogger<Cleaner> logger) : IJobHandler<CleanExpiredRowsPayload>
    {
        public async Task HandleAsync(CleanExpiredRowsPayload payload, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();

            // The expires_at indexes let the first two find their rows. setup_codes has none: it holds a handful of rows
            // (a code is printed by hand on the server), so a scan costs nothing.
            await DrainAsync("idempotency_keys", ct => db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM idempotency_keys WHERE (user_id, "key") IN (
                    SELECT user_id, "key" FROM idempotency_keys
                    WHERE expires_at <= {now}
                    ORDER BY expires_at
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED)
                """, ct), cancellationToken);

            // The ISBN copy's expiry is far in the future, so the date alone would spare it; naming it makes that certain.
            await DrainAsync("provider_cache", ct => db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM provider_cache WHERE (provider, "key") IN (
                    SELECT provider, "key" FROM provider_cache
                    WHERE expires_at <= {now} AND provider <> {ProviderCacheIsbnStore.StoreName}
                    ORDER BY expires_at
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED)
                """, ct), cancellationToken);

            await DrainAsync("setup_codes", ct => db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM setup_codes WHERE id IN (
                    SELECT id FROM setup_codes
                    WHERE used_at IS NOT NULL OR expires_at <= {now}
                    ORDER BY expires_at
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED)
                """, ct), cancellationToken);
        }

        /// <summary>Runs <paramref name="deleteBatch"/> until a batch comes back short (nothing left to take) or the cap is reached.</summary>
        private async Task DrainAsync(string table, Func<CancellationToken, Task<int>> deleteBatch, CancellationToken cancellationToken)
        {
            var total = 0;
            for (var batch = 0; batch < MaxBatchesPerTable; batch++)
            {
                var deleted = await deleteBatch(cancellationToken);
                total += deleted;
                if (deleted < BatchSize)
                {
                    break;
                }
            }

            if (total > 0 && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Removed {Count} expired rows from {Table}.", total, table);
            }
        }
    }
}
