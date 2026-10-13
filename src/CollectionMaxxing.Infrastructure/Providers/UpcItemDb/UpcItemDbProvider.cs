using System.Text.Json;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.Quota;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Providers.UpcItemDb;

/// <summary>
/// <c>Providers:UpcItemDb</c>. Free mode (the default) needs no key: the trial endpoint, 100 lookups a UTC day and about
/// 6 a minute. Setting <see cref="UserKey"/> switches to the paid endpoint, which sends the key in a header, and
/// <see cref="DailyLimit"/> should then be raised to the plan's allowance.
/// </summary>
public sealed class UpcItemDbOptions
{
    public const string SectionName = "Providers:UpcItemDb";

    public Uri BaseUrl { get; set; } = new("https://api.upcitemdb.com/");

    public string? UserKey { get; set; }

    public int DailyLimit { get; set; } = 100;

    /// <summary>The free tier allows about 6 requests a minute.</summary>
    public double RequestsPerSecond { get; set; } = 0.1;
}

/// <summary>How a UPC lookup ended; <see cref="QuotaExhausted"/> lets SCAN-1 mark the code Pending instead of NotFound.</summary>
public enum UpcLookupStatus
{
    Found,
    NotFound,
    QuotaExhausted,
    Unavailable,
}

public sealed record UpcLookupResult(UpcLookupStatus Status, ProviderRelease? Release = null)
{
    public static UpcLookupResult NotFound { get; } = new(UpcLookupStatus.NotFound);

    public static UpcLookupResult QuotaExhausted { get; } = new(UpcLookupStatus.QuotaExhausted);

    public static UpcLookupResult Unavailable { get; } = new(UpcLookupStatus.Unavailable);
}

/// <summary>
/// UPCitemdb: UPC/EAN to a product name and category. Only a real HTTP call spends the daily quota; a cached answer is
/// free. The product name goes through the NameCleaner to pull out title, platform, format and year.
/// </summary>
public sealed class UpcItemDbProvider(
    HttpClient http,
    IOptions<UpcItemDbOptions> options,
    ProviderRuntime runtime,
    ProviderQuotaStore quota)
    : HttpCatalogProvider(http, runtime, ProviderName, TokenBucketRateLimiter.IntervalFor(options.Value.RequestsPerSecond))
{
    public const string ProviderName = "upcitemdb";

    private readonly UpcItemDbOptions _options = options.Value;

    private bool Paid => !string.IsNullOrWhiteSpace(_options.UserKey);

    /// <summary>Product barcodes can be anything on a shelf, so this provider claims every kind.</summary>
    public override bool Supports(WorkKind kind) => true;

    /// <summary>The port's shape: null when the code is unknown, over quota or the provider is down.</summary>
    public override async Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) =>
        (await LookupWithStatusAsync(id, ct)).Release;

    /// <summary>Like <see cref="LookupAsync"/>, but says why there is no release.</summary>
    public async Task<UpcLookupResult> LookupWithStatusAsync(Identifier id, CancellationToken ct)
    {
        if (id.Type is not (IdentifierType.Gtin13 or IdentifierType.Gtin8 or IdentifierType.Isbn13))
        {
            return UpcLookupResult.NotFound;
        }

        // UPC-A codes are sent in their 12-digit form; the GTIN-13 form just has a leading zero.
        var code = id.Type == IdentifierType.Gtin13 && id.Value.StartsWith('0') ? id.Value[1..] : id.Value;
        var path = Paid ? "prod/v1/lookup" : "prod/trial/lookup";
        var result = await FetchAsync(
            $"lookup:{id.Value}",
            _ =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUrl, $"{path}?upc={code}"));
                if (Paid)
                {
                    request.Headers.Add("user_key", _options.UserKey);
                    request.Headers.Add("key_type", "3scale");
                }

                return Task.FromResult(request);
            },
            ct,
            admit: token => quota.TryConsumeAsync(ProviderName, _options.DailyLimit, token));

        switch (result.Status)
        {
            case FetchStatus.Denied:
                return UpcLookupResult.QuotaExhausted;
            case FetchStatus.Failed:
                return UpcLookupResult.Unavailable;
            case FetchStatus.NotFound:
                return UpcLookupResult.NotFound;
        }

        using var json = result.Json!;
        var release = json.RootElement.Items("items").Select(i => FromItem(i, id)).OfType<ProviderRelease>().FirstOrDefault();
        return release is null ? UpcLookupResult.NotFound : new UpcLookupResult(UpcLookupStatus.Found, release);
    }

    /// <summary>UPCitemdb has a search endpoint, but at 100 a day it is kept for barcodes only.</summary>
    public override Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderRelease>>([]);

    private static ProviderRelease? FromItem(JsonElement item, Identifier id)
    {
        var rawTitle = item.Str("title");
        if (rawTitle is null)
        {
            return null;
        }

        var cleaned = NameCleaner.Clean(rawTitle);
        var category = item.Str("category") ?? string.Empty;
        var raw = new[] { item.Str("ean"), item.Str("upc"), item.Str("gtin"), id.Value }.OfType<string>();

        return new ProviderRelease
        {
            Provider = ProviderName,
            ExternalId = item.Str("ean") ?? item.Str("upc") ?? id.Value,
            Title = cleaned.Title.Length > 0 ? cleaned.Title : rawTitle,
            Kind = KindOf(category),
            Format = cleaned.Format,
            Platform = cleaned.Platform,
            Year = cleaned.Year,
            Publisher = item.Str("brand"),
            Synopsis = item.Str("description"),
            CoverUrl = item.Strings("images").Select(CoverHosts.Allow).OfType<string>().FirstOrDefault(),
            Identifiers = ProviderJson.Identifiers(raw),
        };
    }

    private static WorkKind KindOf(string category)
    {
        if (category.Contains("Video Games", StringComparison.OrdinalIgnoreCase))
        {
            return WorkKind.Game;
        }

        if (category.Contains("Books", StringComparison.OrdinalIgnoreCase))
        {
            return WorkKind.Book;
        }

        if (category.Contains("TV", StringComparison.OrdinalIgnoreCase) && category.Contains("DVD", StringComparison.OrdinalIgnoreCase))
        {
            return WorkKind.Tv;
        }

        return category.Contains("Movies", StringComparison.OrdinalIgnoreCase) || category.Contains("DVD", StringComparison.OrdinalIgnoreCase)
            ? WorkKind.Film
            : WorkKind.Other;
    }
}
