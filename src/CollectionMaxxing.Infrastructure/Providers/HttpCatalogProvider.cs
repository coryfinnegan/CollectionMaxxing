using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.Caching;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Infrastructure.Providers;

/// <summary>The singletons every provider adapter shares.</summary>
public sealed class ProviderRuntime(
    TimeProvider time,
    ProviderResponseCache cache,
    ProviderRateLimiters limiters,
    ILoggerFactory loggers)
{
    public TimeProvider Time { get; } = time;

    public ProviderResponseCache Cache { get; } = cache;

    public ProviderRateLimiters Limiters { get; } = limiters;

    public ILoggerFactory Loggers { get; } = loggers;
}

/// <summary>How a provider call ended.</summary>
public enum FetchStatus
{
    /// <summary>The provider answered; <see cref="FetchResult.Json"/> holds the body.</summary>
    Found,

    /// <summary>The provider does not know it (404, or cached as unknown).</summary>
    NotFound,

    /// <summary>The call was refused before it was sent (the day's quota is spent).</summary>
    Denied,

    /// <summary>The provider could not be reached or gave an unusable answer. Never cached.</summary>
    Failed,
}

public readonly record struct FetchResult(FetchStatus Status, JsonDocument? Json)
{
    public static FetchResult NotFound { get; } = new(FetchStatus.NotFound, null);

    public static FetchResult Denied { get; } = new(FetchStatus.Denied, null);

    public static FetchResult Failed { get; } = new(FetchStatus.Failed, null);
}

/// <summary>
/// The common path of every adapter: cache first, then the rate limiter, then the HTTP call through the resilience
/// pipeline, then the cache write. A failure never throws and is never cached: the provider simply "did not answer".
/// Messages never contain a request URL or header, so keys stay out of the logs.
/// </summary>
public abstract class HttpCatalogProvider(HttpClient http, ProviderRuntime runtime, string name, TimeSpan minInterval)
    : ICatalogProvider
{
    private readonly ILogger _logger = runtime.Loggers.CreateLogger($"Providers.{name}");

    public string Name { get; } = name;

    public abstract bool Supports(WorkKind kind);

    public abstract Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct);

    public abstract Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct);

    protected ProviderRuntime Runtime { get; } = runtime;

    /// <summary>
    /// Gets JSON from the provider, through the cache. <paramref name="admit"/> runs just before a real call is sent (the
    /// quota check). <paramref name="refreshAuth"/> runs once on a 401, and returns true when the request is worth
    /// sending again with fresh credentials. With <paramref name="cacheNotFound"/> false a 404 (and an answer
    /// <paramref name="isEmpty"/> calls empty) is not remembered, so the same code is asked again next time.
    /// </summary>
    protected async Task<FetchResult> FetchAsync(
        string cacheKey,
        Func<CancellationToken, Task<HttpRequestMessage>> buildRequest,
        CancellationToken ct,
        Func<CancellationToken, Task<bool>>? admit = null,
        Func<CancellationToken, Task<bool>>? refreshAuth = null,
        bool cacheNotFound = true,
        Func<JsonDocument, bool>? isEmpty = null)
    {
        var cached = await Runtime.Cache.TryGetAsync(Name, cacheKey, ct);
        if (cached is not null)
        {
            return cached == ProviderResponseCache.NotFoundPayload
                ? FetchResult.NotFound
                : new FetchResult(FetchStatus.Found, JsonDocument.Parse(cached));
        }

        if (admit is not null && !await admit(ct))
        {
            return FetchResult.Denied;
        }

        try
        {
            var refreshed = false;
            while (true)
            {
                await Runtime.Limiters.For(Name, minInterval).WaitAsync(ct);
                using var request = await buildRequest(ct);
                using var response = await http.SendAsync(request, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    && !refreshed
                    && refreshAuth is not null
                    && await refreshAuth(ct))
                {
                    refreshed = true; // once, never in a loop
                    continue;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    if (cacheNotFound)
                    {
                        await Runtime.Cache.SetAsync(Name, cacheKey, ProviderResponseCache.NotFoundPayload, ct);
                    }

                    return FetchResult.NotFound;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("{Provider} answered HTTP {Status}", Name, (int)response.StatusCode);
                    return FetchResult.Failed;
                }

                // A provider is untrusted: an answer bigger than any real one is dropped, not read into memory or kept.
                if (response.Content.Headers.ContentLength > MaxBodyBytes)
                {
                    _logger.LogWarning("{Provider} sent an answer over the size limit", Name);
                    return FetchResult.Failed;
                }

                var body = await ReadCappedAsync(response.Content, ct);
                if (body is null)
                {
                    _logger.LogWarning("{Provider} sent an answer over the size limit", Name);
                    return FetchResult.Failed;
                }

                var json = JsonDocument.Parse(body); // a malformed body throws JsonException, handled below
                if (cacheNotFound || isEmpty?.Invoke(json) != true)
                {
                    await Runtime.Cache.SetAsync(Name, cacheKey, body, ct);
                }

                return new FetchResult(FetchStatus.Found, json);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A provider that cannot answer must never take the caller down: timeouts, open circuits and bad bodies all end here.
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException
            or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException)
#pragma warning restore CA1031
        {
            _logger.LogWarning("{Provider} did not answer: {Reason}", Name, ex.GetType().Name);
            return FetchResult.Failed;
        }
    }

    /// <summary>The largest provider answer read (real answers are a few KB to a few hundred KB).</summary>
    public const int MaxBodyBytes = 2 * 1024 * 1024;

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        // A UTF-8 byte order mark is not part of the JSON text (Encoding.GetString would keep it as U+FEFF, which the parser rejects).
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        return Encoding.UTF8.GetString(bytes.StartsWith(Encoding.UTF8.Preamble) ? bytes[Encoding.UTF8.Preamble.Length..] : bytes);
    }

    /// <summary>A short, fixed-length cache key for a search, so long titles never overflow the 500-character column.</summary>
    protected static string SearchKey(params object?[] parts)
    {
        var text = string.Join('|', parts.Select(p => p?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty));
        return "search:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
