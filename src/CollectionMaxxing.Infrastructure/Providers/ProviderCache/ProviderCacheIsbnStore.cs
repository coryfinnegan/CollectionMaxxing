using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.Providers.Caching;

/// <summary>
/// The permanent local copy of found ISBNs, kept in <c>provider_cache</c> under the provider name <see cref="StoreName"/> with an
/// expiry no clock reaches. It has its own scope per call, so a batch of codes can use it side by side.
/// </summary>
public sealed class ProviderCacheIsbnStore(IServiceScopeFactory scopes, TimeProvider time) : IIsbnStore
{
    public const string StoreName = "isbn";

    /// <summary>"Never": far past anything a lookup or the cache cleanup compares against.</summary>
    private static readonly DateTimeOffset Forever = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<ProviderRelease?> FindAsync(string isbn13, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payload = await db.ProviderCache.AsNoTracking()
            .Where(c => c.Provider == StoreName && c.Key == isbn13)
            .Select(c => c.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredBook>(payload, Json)?.ToRelease();
        }
        catch (JsonException)
        {
            return null; // an unreadable row is a miss; the next found answer replaces it
        }
    }

    public async Task SaveAsync(string isbn13, ProviderRelease release, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(StoredBook.From(release), Json);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();

        var entry = await db.ProviderCache.FirstOrDefaultAsync(c => c.Provider == StoreName && c.Key == isbn13, ct);
        if (entry is null)
        {
            db.ProviderCache.Add(new ProviderCacheEntry
            {
                Provider = StoreName,
                Key = isbn13,
                PayloadJson = payload,
                FetchedAt = now,
                ExpiresAt = Forever,
            });
        }
        else
        {
            entry.PayloadJson = payload;
            entry.FetchedAt = now;
            entry.ExpiresAt = Forever;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request saved the same book a moment ago; either copy is fine.
        }
    }

    /// <summary>The stored shape: plain strings and numbers, so a change to <see cref="ProviderRelease"/> never breaks old rows.</summary>
    private sealed record StoredBook(
        string Provider, string ExternalId, string Title, WorkKind Kind, ReleaseFormat? Format, string? Platform, int? Year,
        List<string> Creators, string? Edition, string? Publisher, string? CoverUrl, string? Synopsis, List<string> Identifiers)
    {
        public static StoredBook From(ProviderRelease r) => new(
            r.Provider, r.ExternalId, r.Title, r.Kind, r.Format, r.Platform, r.Year, [.. r.Creators], r.Edition, r.Publisher,
            r.CoverUrl, r.Synopsis, [.. r.Identifiers.Select(i => i.Value)]);

        public ProviderRelease ToRelease() => new()
        {
            Provider = Provider,
            ExternalId = ExternalId,
            Title = Title,
            Kind = Kind,
            Format = Format,
            Platform = Platform,
            Year = Year,
            Creators = Creators ?? [],
            Edition = Edition,
            Publisher = Publisher,
            CoverUrl = CoverUrl,
            Synopsis = Synopsis,
            Identifiers = ProviderJson.Identifiers(Identifiers ?? []),
        };
    }
}
