using System.Text.Json;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Providers.OpenLibrary;

/// <summary><c>Providers:OpenLibrary</c>. No key; an identifying User-Agent with a contact email raises the limit to 3 a second.</summary>
public sealed class OpenLibraryOptions
{
    public const string SectionName = "Providers:OpenLibrary";

    public Uri BaseUrl { get; set; } = new("https://openlibrary.org/");

    /// <summary>Where Open Library can reach the operator. Without it the anonymous 1 request a second applies.</summary>
    public string? ContactEmail { get; set; }

    public double RequestsPerSecond { get; set; } = 3;
}

/// <summary>
/// Open Library: ISBN lookup (<c>/api/books</c>, which includes author names in one call) and title/author search
/// (<c>/search.json</c>). Primary provider for books.
/// </summary>
public sealed class OpenLibraryProvider(HttpClient http, IOptions<OpenLibraryOptions> options, ProviderRuntime runtime)
    : HttpCatalogProvider(http, runtime, ProviderName, Interval(options.Value))
{
    public const string ProviderName = "openlibrary";

    private readonly OpenLibraryOptions _options = options.Value;

    public static string UserAgent(OpenLibraryOptions options) =>
        string.IsNullOrWhiteSpace(options.ContactEmail)
            ? "CollectionMaxxing/1.0"
            : $"CollectionMaxxing/1.0 ({options.ContactEmail.Trim()})";

    public override bool Supports(WorkKind kind) => kind == WorkKind.Book;

    public override async Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct)
    {
        if (id.Type != IdentifierType.Isbn13)
        {
            return null;
        }

        var bibkey = $"ISBN:{id.Value}";
        var result = await FetchAsync(
            $"lookup:{bibkey}",
            _ => Task.FromResult(Get($"api/books?bibkeys={bibkey}&format=json&jscmd=data")),
            ct);
        if (result.Json is null)
        {
            return null;
        }

        using var json = result.Json;
        return json.RootElement.Prop(bibkey) is { } book ? FromBook(book, id) : null;
    }

    public override async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
    {
        if (query.Kind is not (null or WorkKind.Book) || string.IsNullOrWhiteSpace(query.Title))
        {
            return [];
        }

        var url = $"search.json?title={Uri.EscapeDataString(query.Title)}&limit={query.Limit}"
            + "&fields=key,title,author_name,first_publish_year,isbn,cover_i,publisher"
            + (string.IsNullOrWhiteSpace(query.Creator) ? string.Empty : $"&author={Uri.EscapeDataString(query.Creator)}");
        var result = await FetchAsync(
            SearchKey("title", query.Title, query.Creator, query.Limit),
            _ => Task.FromResult(Get(url)),
            ct);
        if (result.Json is null)
        {
            return [];
        }

        using var json = result.Json;
        return json.RootElement.Items("docs").Select(FromSearchDoc).OfType<ProviderRelease>().Take(query.Limit).ToList();
    }

    private HttpRequestMessage Get(string relativeUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUrl, relativeUrl));
        request.Headers.UserAgent.ParseAdd(UserAgent(_options));
        return request;
    }

    private static TimeSpan Interval(OpenLibraryOptions options) =>
        TokenBucketRateLimiter.IntervalFor(string.IsNullOrWhiteSpace(options.ContactEmail) ? Math.Min(options.RequestsPerSecond, 1) : options.RequestsPerSecond);

    private static ProviderRelease? FromBook(JsonElement book, Identifier id)
    {
        var title = book.Str("title");
        if (title is null)
        {
            return null;
        }

        var subtitle = book.Str("subtitle");
        var key = book.Str("key")?.Split('/').LastOrDefault() ?? id.Value;
        var ids = book.Prop("identifiers") is { } identifiers
            ? identifiers.Strings("isbn_13").Concat(identifiers.Strings("isbn_10"))
            : [];

        return new ProviderRelease
        {
            Provider = ProviderName,
            ExternalId = key,
            Title = subtitle is null ? title : $"{title}: {subtitle}",
            Kind = WorkKind.Book,
            Format = FormatOf(book.Str("physical_format")),
            Year = ProviderJson.Year(book.Str("publish_date")),
            Creators = book.Items("authors").Select(a => a.Str("name")).OfType<string>().ToList(),
            Publisher = book.Items("publishers").Select(p => p.Str("name")).OfType<string>().FirstOrDefault(),
            CoverUrl = CoverHosts.Allow(book.Prop("cover")?.Str("large") ?? book.Prop("cover")?.Str("medium")),
            Identifiers = ProviderJson.Identifiers(ids.Prepend(id.Value)),
        };
    }

    private static ProviderRelease? FromSearchDoc(JsonElement doc)
    {
        var title = doc.Str("title");
        var key = doc.Str("key")?.Split('/').LastOrDefault();
        if (title is null || key is null)
        {
            return null;
        }

        return new ProviderRelease
        {
            Provider = ProviderName,
            ExternalId = key,
            Title = title,
            Kind = WorkKind.Book,
            Year = doc.Num("first_publish_year") is { } year ? (int)year : null,
            Creators = doc.Strings("author_name").ToList(),
            Publisher = doc.Strings("publisher").FirstOrDefault(),
            CoverUrl = doc.Num("cover_i") is { } cover
                ? CoverHosts.Allow($"https://covers.openlibrary.org/b/id/{cover}-L.jpg")
                : null,
            Identifiers = ProviderJson.Identifiers(doc.Strings("isbn").Take(20)).Take(3).ToList(),
        };
    }

    private static ReleaseFormat? FormatOf(string? physicalFormat) => physicalFormat?.ToLowerInvariant() switch
    {
        null => null,
        var f when f.Contains("mass market", StringComparison.Ordinal) => ReleaseFormat.MassMarket,
        var f when f.Contains("hardcover", StringComparison.Ordinal) || f.Contains("hardback", StringComparison.Ordinal) => ReleaseFormat.Hardcover,
        var f when f.Contains("paperback", StringComparison.Ordinal) || f.Contains("softcover", StringComparison.Ordinal) => ReleaseFormat.Paperback,
        _ => null,
    };
}
