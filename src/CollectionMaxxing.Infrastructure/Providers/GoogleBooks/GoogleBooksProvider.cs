using System.Text.Json;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Providers.GoogleBooks;

/// <summary><c>Providers:GoogleBooks</c>. Needs an API key; without one the provider is switched off.</summary>
public sealed class GoogleBooksOptions
{
    public const string SectionName = "Providers:GoogleBooks";

    public Uri BaseUrl { get; set; } = new("https://www.googleapis.com/");

    public string? ApiKey { get; set; }

    public double RequestsPerSecond { get; set; } = 5;
}

/// <summary>Google Books: ISBN fallback and title search. The key travels in a header, never in the URL.</summary>
public sealed class GoogleBooksProvider(HttpClient http, IOptions<GoogleBooksOptions> options, ProviderRuntime runtime)
    : HttpCatalogProvider(http, runtime, ProviderName, TokenBucketRateLimiter.IntervalFor(options.Value.RequestsPerSecond)),
    ISearchableCatalogProvider
{
    public const string ProviderName = "googlebooks";

    private readonly GoogleBooksOptions _options = options.Value;

    private bool Enabled => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public override bool Supports(WorkKind kind) => Enabled && kind == WorkKind.Book;

    public override async Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct)
    {
        if (!Enabled || id.Type != IdentifierType.Isbn13)
        {
            return null;
        }

        // q=isbn: stays: bare digit strings answer 0 too (#511), so there is no fallback to try, and an empty
        // lookup is never cached, so a GB recovery heals this with no deploy.
        var result = await FetchAsync(
            $"lookup:isbn:{id.Value}",
            _ => Task.FromResult(Get($"books/v1/volumes?q=isbn:{id.Value}&maxResults=1")),
            ct,
            cacheNotFound: false, // a barcode Google does not know today may be known later
            isEmpty: doc => !doc.RootElement.Items("items").Any());
        if (result.Json is null)
        {
            return null;
        }

        using var json = result.Json;
        return json.RootElement.Items("items").Select(FromVolume).OfType<ProviderRelease>().FirstOrDefault();
    }

    public override async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
    {
        if (!Enabled || query.Kind is not (null or WorkKind.Book) || string.IsNullOrWhiteSpace(query.Title))
        {
            return [];
        }

        // GB's field operators (intitle:/inauthor:) currently answer totalItems 0 while bare word search works (#511):
        // send bare words and let the scorer match client-side.
        var q = query.Title.Trim() + (string.IsNullOrWhiteSpace(query.Creator) ? string.Empty : $" {query.Creator.Trim()}");
        var url = $"books/v1/volumes?q={Uri.EscapeDataString(q)}&maxResults={Math.Clamp(query.Limit, 1, 40)}";
        var result = await FetchAsync(
            SearchKey("title", query.Title, query.Creator, query.Limit),
            _ => Task.FromResult(Get(url)),
            ct,
            isEmpty: doc => !doc.RootElement.Items("items").Any());
        if (result.Json is null)
        {
            return [];
        }

        using var json = result.Json;
        return json.RootElement.Items("items").Select(FromVolume).OfType<ProviderRelease>().Take(query.Limit).ToList();
    }

    private HttpRequestMessage Get(string relativeUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUrl, relativeUrl));
        request.Headers.Add("x-goog-api-key", _options.ApiKey);
        return request;
    }

    private static ProviderRelease? FromVolume(JsonElement volume)
    {
        var id = volume.Str("id");
        var info = volume.Prop("volumeInfo");
        var title = info?.Str("title");
        if (id is null || info is not { } details || title is null)
        {
            return null;
        }

        var subtitle = details.Str("subtitle");
        var identifiers = details.Items("industryIdentifiers").Select(i => i.Str("identifier")).OfType<string>();

        return new ProviderRelease
        {
            Provider = ProviderName,
            ExternalId = id,
            Title = subtitle is null ? title : $"{title}: {subtitle}",
            Kind = WorkKind.Book,
            Year = ProviderJson.Year(details.Str("publishedDate")),
            Creators = details.Strings("authors").ToList(),
            Publisher = details.Str("publisher"),
            Synopsis = details.Str("description"),
            CoverUrl = CoverHosts.Allow(details.Prop("imageLinks")?.Str("thumbnail") ?? details.Prop("imageLinks")?.Str("smallThumbnail")),
            Identifiers = ProviderJson.Identifiers(identifiers),
        };
    }
}
