using System.Text.Json;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.Providers.Caching;

/// <summary>
/// The provider hits a search showed, kept in <c>provider_cache</c> under the name <see cref="StoreName"/> for
/// <see cref="Lifetime"/> and keyed by <c>provider:externalId</c>. Each call has its own scope (like the ISBN copy), so a
/// search can save its hits while its own request carries on. Saving also clears the hits whose day has passed, so the
/// table stays about as big as a day of searching.
/// </summary>
public sealed class ProviderCacheHitStore(IServiceScopeFactory scopes, TimeProvider time) : IProviderHitStore
{
    public const string StoreName = "hit";

    /// <summary>
    /// How long a shown hit can be added: a person adds within minutes, and a day covers a sheet left open overnight. After
    /// that the app says "search again" and the next search shows the hit afresh.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    /// <summary>The length of <c>provider_cache.key</c>. A hit whose key is longer cannot be kept and is skipped.</summary>
    private const int MaxKeyLength = 500;

    /// <summary>Two searches saving overlapping new hits at once: the loser of a row retries, now seeing the other's rows.</summary>
    private const int Attempts = 3;

    public async Task SaveAsync(IReadOnlyCollection<ProviderRelease> hits, CancellationToken ct)
    {
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            var key = KeyFor(hit.Provider, hit.ExternalId);
            if (key.Length <= MaxKeyLength)
            {
                payloads[key] = JsonSerializer.Serialize(StoredProviderRelease.From(hit), StoredProviderRelease.Json);
            }
        }

        if (payloads.Count == 0)
        {
            return;
        }

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (await TrySaveAsync(payloads, ct))
            {
                return;
            }
        }
    }

    public async Task<ProviderRelease?> FindAsync(string provider, string externalId, CancellationToken ct)
    {
        var key = KeyFor(provider, externalId);
        if (key.Length > MaxKeyLength)
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var payload = await db.ProviderCache.AsNoTracking()
            .Where(c => c.Provider == StoreName && c.Key == key && c.ExpiresAt > now)
            .Select(c => c.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredProviderRelease>(payload, StoredProviderRelease.Json)?.ToRelease();
        }
        catch (JsonException)
        {
            return null; // an unreadable row is a miss; the next search that shows the hit replaces it
        }
    }

    private static string KeyFor(string provider, string externalId) => $"{provider}:{externalId}";

    /// <summary>One try at saving the payloads in one go. False when another search inserted one of the same rows first.</summary>
    private async Task<bool> TrySaveAsync(Dictionary<string, string> payloads, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        await db.ProviderCache.Where(c => c.Provider == StoreName && c.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        var keys = payloads.Keys.ToList();
        var existing = await db.ProviderCache
            .Where(c => c.Provider == StoreName && keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, StringComparer.Ordinal, ct);
        foreach (var (key, payload) in payloads)
        {
            if (existing.TryGetValue(key, out var entry))
            {
                entry.PayloadJson = payload;
                entry.FetchedAt = now;
                entry.ExpiresAt = now + Lifetime;
            }
            else
            {
                db.ProviderCache.Add(new ProviderCacheEntry
                {
                    Provider = StoreName,
                    Key = key,
                    PayloadJson = payload,
                    FetchedAt = now,
                    ExpiresAt = now + Lifetime,
                });
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }
}
