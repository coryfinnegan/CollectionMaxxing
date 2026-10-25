using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.Providers.Caching;

/// <summary>
/// The provider response cache in <c>provider_cache</c>: full answers live 30 days, empty ("no results") answers live a
/// day. It stores what the provider said, so a repeat lookup makes no HTTP call. A provider that answered "not known"
/// is cached too (payload <see cref="NotFoundPayload"/>), so a missing barcode does not spend quota again.
/// </summary>
public sealed class ProviderResponseCache(IServiceScopeFactory scopes, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// How long an empty ("no results") answer is remembered: long enough to protect quota, short enough that
    /// something the provider catalogues tomorrow is found tomorrow.
    /// </summary>
    public static readonly TimeSpan NegativeLifetime = TimeSpan.FromDays(1);

    /// <summary>The stored payload for "the provider does not know this".</summary>
    public const string NotFoundPayload = "null";

    /// <summary>The cached payload, or null when there is none or it has expired.</summary>
    public async Task<string?> TryGetAsync(string provider, string key, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        return await db.ProviderCache.AsNoTracking()
            .Where(c => c.Provider == provider && c.Key == key && c.ExpiresAt > now)
            .Select(c => c.PayloadJson)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SetAsync(string provider, string key, string payloadJson, CancellationToken ct, TimeSpan? lifetime = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var expiresAt = now + (lifetime ?? Lifetime);

        var entry = await db.ProviderCache.FirstOrDefaultAsync(c => c.Provider == provider && c.Key == key, ct);
        if (entry is null)
        {
            db.ProviderCache.Add(new ProviderCacheEntry
            {
                Provider = provider,
                Key = key,
                PayloadJson = payloadJson,
                FetchedAt = now,
                ExpiresAt = expiresAt,
            });
        }
        else
        {
            entry.PayloadJson = payloadJson;
            entry.FetchedAt = now;
            entry.ExpiresAt = expiresAt;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request cached the same answer a moment ago; either copy is fine.
        }
    }
}
