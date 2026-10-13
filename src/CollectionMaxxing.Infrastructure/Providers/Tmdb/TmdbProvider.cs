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
    : HttpCatalogProvider(http, runtime, ProviderName, TokenBucketRateLimiter.IntervalFor(options.Value.RequestsPerSecond))
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

        var found = new List<ProviderRelease>();
        if (query.Kind is null or WorkKind.Film)
        {
            found.AddRange(await SearchKindAsync(WorkKind.Film, query, ct));
        }

        if (query.Kind is null or WorkKind.Tv)
        {
            found.AddRange(await SearchKindAsync(WorkKind.Tv, query, ct));
        }

        return found.Take(query.Limit).ToList();
    }

    private async Task<IReadOnlyList<ProviderRelease>> SearchKindAsync(WorkKind kind, CatalogQuery query, CancellationToken ct)
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
            ct);
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

        return new ProviderRelease
        {
            Provider = ProviderName,
            ExternalId = $"{(film ? "movie" : "tv")}/{id}",
            Title = title,
            Kind = kind,
            Year = ProviderJson.Year(result.Str(film ? "release_date" : "first_air_date")),
            Synopsis = result.Str("overview"),
            CoverUrl = result.Str("poster_path") is { } poster && poster.StartsWith('/') ? CoverHosts.Allow(PosterBase + poster) : null,
        };
    }
}
