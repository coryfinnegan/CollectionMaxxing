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
/// Open Library: ISBN lookup (<c>/isbn/{isbn}.json</c>, then one call per author for the name) and title/author search
/// (<c>/search.json</c>). Primary provider for books.
/// </summary>
public sealed class OpenLibraryProvider(HttpClient http, IOptions<OpenLibraryOptions> options, ProviderRuntime runtime)
    : HttpCatalogProvider(http, runtime, ProviderName, Interval(options.Value)),
    ISearchableCatalogProvider
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

        // The direct edition endpoint: much faster than /api/books, but it names authors by key only.
        var result = await FetchAsync(
            $"lookup:isbn:{id.Value}",
            _ => Task.FromResult(Get($"isbn/{id.Value}.json")),
            ct,
            cacheNotFound: false, // a barcode Open Library does not know today may be known later
            isEmpty: doc => doc.RootElement.Str("title") is null);
        if (result.Json is null)
        {
            return null;
        }

        using var json = result.Json;
        var edition = json.RootElement;
        if (edition.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var authors = await AuthorNamesAsync(edition, ct);
        return FromEdition(edition, id, authors);
    }

    private const int MaxAuthors = 5;

    /// <summary>The names behind the edition's author keys. An author that cannot be fetched is left out; the book is still found.</summary>
    private async Task<IReadOnlyList<string>> AuthorNamesAsync(JsonElement edition, CancellationToken ct)
    {
        var keys = edition.Items("authors")
            .Select(a => a.Str("key"))
            .OfType<string>()
            .Where(k => k.StartsWith("/authors/OL", StringComparison.Ordinal) && k.All(c => char.IsLetterOrDigit(c) || c is '/'))
            .Take(MaxAuthors)
            .ToList();
        var names = await Task.WhenAll(keys.Select(async key =>
        {
            var result = await FetchAsync($"author:{key}", _ => Task.FromResult(Get($"{key.TrimStart('/')}.json")), ct);
            if (result.Json is null)
            {
                return null;
            }

            using var author = result.Json;
            return author.RootElement.Str("name");
        }));
        return names.OfType<string>().ToList();
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
            ct,
            isEmpty: doc => !doc.RootElement.Items("docs").Any());
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

    private static ProviderRelease? FromEdition(JsonElement edition, Identifier id, IReadOnlyList<string> authors)
    {
        var title = edition.Str("title");
        if (title is null)
        {
            return null;
        }

        var subtitle = edition.Str("subtitle");
        var key = edition.Str("key")?.Split('/').LastOrDefault() ?? id.Value;
        var ids = edition.Strings("isbn_13").Concat(edition.Strings("isbn_10"));
        var cover = edition.Items("covers").FirstOrDefault(c => c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var n) && n > 0);

        return new ProviderRelease
        {
            Provider = ProviderName,
            ExternalId = key,
            Title = subtitle is null ? title : $"{title}: {subtitle}",
            Kind = WorkKind.Book,
            Format = FormatOf(edition.Str("physical_format")),
            Year = ProviderJson.Year(edition.Str("publish_date")),
            Creators = authors,
            Publisher = edition.Strings("publishers").FirstOrDefault(),
            CoverUrl = cover.ValueKind == JsonValueKind.Number
                ? CoverHosts.Allow($"https://covers.openlibrary.org/b/id/{cover.GetInt64()}-L.jpg")
                : null,
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
            WorkKey = key, // a /search.json doc is a work, so its key is the work key
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

    /// <summary>
    /// The edition's binding in our taxonomy: null when the edition does not name one (unknown, never Other),
    /// <see cref="ReleaseFormat.Other"/> only when it names something that fits no category (an audiobook disc).
    /// </summary>
    private static ReleaseFormat? FormatOf(string? physicalFormat) => physicalFormat?.ToLowerInvariant() switch
    {
        null => null,
        var f when string.IsNullOrWhiteSpace(f) => null,
        var f when f.Contains("mass market", StringComparison.Ordinal) => ReleaseFormat.MassMarket,
        var f when f.Contains("hardcover", StringComparison.Ordinal) || f.Contains("hardback", StringComparison.Ordinal) => ReleaseFormat.Hardcover,
        var f when f.Contains("paperback", StringComparison.Ordinal) || f.Contains("softcover", StringComparison.Ordinal) => ReleaseFormat.Paperback,
        var f when f.Contains("board book", StringComparison.Ordinal) || f.Contains("boardbook", StringComparison.Ordinal) => ReleaseFormat.BoardBook,
        var f when f.Contains("spiral", StringComparison.Ordinal) => ReleaseFormat.SpiralBound,
        _ => ReleaseFormat.Other,
    };
}
