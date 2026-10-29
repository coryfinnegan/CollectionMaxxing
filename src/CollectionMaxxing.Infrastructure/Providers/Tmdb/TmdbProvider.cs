using System.Net.Http.Headers;
using System.Text.Json;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Providers.Tmdb;

/// <summary>
/// <c>Providers:Tmdb</c>. <see cref="ApiKey"/> is either the "API Read Access Token" (a long value starting <c>eyJ</c>, sent
/// as a bearer header) or the short v3 "API Key" (sent as the <c>api_key</c> query value). Without one the provider is off.
/// </summary>
public sealed class TmdbOptions
{
    public const string SectionName = "Providers:Tmdb";

    public Uri BaseUrl { get; set; } = new("https://api.themoviedb.org/");

    public string? ApiKey { get; set; }

    public double RequestsPerSecond { get; set; } = 20;
}

/// <summary>TMDB: film and TV by title and year. There is no barcode lookup, so <c>LookupAsync</c> is always null.</summary>
public sealed class TmdbProvider(HttpClient http, IOptions<TmdbOptions> options, ProviderRuntime runtime)
    : HttpCatalogProvider(http, runtime, ProviderName, TokenBucketRateLimiter.IntervalFor(options.Value.RequestsPerSecond)),
    ISearchableCatalogProvider
{
    public const string ProviderName = "tmdb";

    private const string PosterBase = "https://image.tmdb.org/t/p/w500";

    private readonly TmdbOptions _options = options.Value;

    private bool Enabled => !string.IsNullOrWhiteSpace(_options.ApiKey);

    private bool IsBearer => _options.ApiKey!.StartsWith("eyJ", StringComparison.Ordinal);

    public override bool Supports(WorkKind kind) => Enabled && kind is WorkKind.Film or WorkKind.Tv;

    public override Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

    public override async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
    {
        if (!Enabled || query.Kind is not (null or WorkKind.Film or WorkKind.Tv) || string.IsNullOrWhiteSpace(query.Title))
        {
            return [];
        }

        // A DVD or a tape holds a film or a show, and the reader can't tell which from the case: both are searched, the
        // kind it named first. Round-robin before the limit cuts: appending a full page of one would starve the other (#292).
        var films = await SearchKindAsync(WorkKind.Film, query, ct);
        var shows = await SearchKindAsync(WorkKind.Tv, query, ct);
        return (query.Kind == WorkKind.Tv ? Interleave(shows, films) : Interleave(films, shows)).Take(query.Limit).ToList();
    }

    private static IEnumerable<ProviderRelease> Interleave(IReadOnlyList<ProviderRelease> first, IReadOnlyList<ProviderRelease> second)
    {
        for (var i = 0; i < first.Count || i < second.Count; i++)
        {
            if (i < first.Count)
            {
                yield return first[i];
            }

            if (i < second.Count)
            {
                yield return second[i];
            }
        }
    }

    /// <summary>
    /// TMDB's year is a filter, and a year read off a case or a product name is often the disc's year, not the film's
    /// (a 2008 Blu-ray of a 1979 film). A year that finds nothing is asked again without it; the scorer still weighs it.
    /// </summary>
    private async Task<IReadOnlyList<ProviderRelease>> SearchKindAsync(WorkKind kind, CatalogQuery query, CancellationToken ct)
    {
        var found = await SearchKindOnceAsync(kind, query, ct);
        return found.Count == 0 && query.Year is not null ? await SearchKindOnceAsync(kind, query with { Year = null }, ct) : found;
    }

    private async Task<IReadOnlyList<ProviderRelease>> SearchKindOnceAsync(WorkKind kind, CatalogQuery query, CancellationToken ct)
    {
        var film = kind == WorkKind.Film;
        var url = $"3/search/{(film ? "movie" : "tv")}?query={Uri.EscapeDataString(query.Title)}&include_adult=false";
        if (query.Year is { } year)
        {
            url += $"&{(film ? "year" : "first_air_date_year")}={year}";
        }

        if (!IsBearer)
        {
            url += $"&api_key={Uri.EscapeDataString(_options.ApiKey!)}";
        }

        var result = await FetchAsync(
            SearchKey(kind, query.Title, query.Year, query.Limit),
            _ => Task.FromResult(Get(url)),
            ct,
            isEmpty: doc => !doc.RootElement.Items("results").Any());
        if (result.Json is null)
        {
            return [];
        }

        using var json = result.Json;
        return json.RootElement.Items("results").Select(r => FromResult(r, kind)).OfType<ProviderRelease>().ToList();
    }

    private HttpRequestMessage Get(string relativeUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUrl, relativeUrl));
        if (IsBearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        return request;
    }

    private static ProviderRelease? FromResult(JsonElement result, WorkKind kind)
    {
        var film = kind == WorkKind.Film;
        var id = result.Num("id");
        var title = result.Str(film ? "title" : "name");
        if (id is null || title is null)
        {
            return null;
        }

        var externalId = $"{(film ? "movie" : "tv")}/{id}";
        return new ProviderRelease
        {
            Provider = ProviderName,
            ExternalId = externalId,
            WorkKey = externalId, // a TMDB movie or show is a work
            Title = title,
            Kind = kind,
            Year = ProviderJson.Year(result.Str(film ? "release_date" : "first_air_date")),
            Synopsis = result.Str("overview"),
            CoverUrl = result.Str("poster_path") is { } poster && poster.StartsWith('/') ? CoverHosts.Allow(PosterBase + poster) : null,
        };
    }
}
