using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Providers.Igdb;

/// <summary>
/// <c>Providers:Igdb</c>. The Twitch application's client id and secret; without both the provider is off. They stay on
/// the server: the app token is fetched with the client-credentials flow and cached in memory.
/// </summary>
public sealed class IgdbOptions
{
    public const string SectionName = "Providers:Igdb";

    public Uri BaseUrl { get; set; } = new("https://api.igdb.com/");

    public Uri TokenUrl { get; set; } = new("https://id.twitch.tv/oauth2/token");

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public double RequestsPerSecond { get; set; } = 4;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>The Twitch app access token, kept in memory until shortly before it expires.</summary>
public sealed class IgdbTokenSource(HttpClient http, IOptions<IgdbOptions> options, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string?> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _token is not null && time.GetUtcNow() < _expiresAt ? _token : await FetchAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Throws the cached token away and fetches a new one. True when a new token was obtained.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _token = null;
            return await FetchAsync(ct) is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> FetchAsync(CancellationToken ct)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = settings.ClientSecret!,
                ["grant_type"] = "client_credentials",
            }),
        };

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var token = json.RootElement.Str("access_token");
            if (token is null)
            {
                return null;
            }

            var lifetime = TimeSpan.FromSeconds(json.RootElement.Num("expires_in") ?? 3600);
            _token = token;
            _expiresAt = time.GetUtcNow() + lifetime - Margin;
            return token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // No token means "the provider did not answer"; the reason does not matter to the caller.
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException
            or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}

/// <summary>IGDB: games by title. No barcode lookup, so <c>LookupAsync</c> is always null.</summary>
public sealed class IgdbProvider(HttpClient http, IOptions<IgdbOptions> options, IgdbTokenSource tokens, ProviderRuntime runtime)
    : HttpCatalogProvider(http, runtime, ProviderName, TokenBucketRateLimiter.IntervalFor(options.Value.RequestsPerSecond)),
    ISearchableCatalogProvider
{
    public const string ProviderName = "igdb";

    private const string Fields =
        "name,first_release_date,summary,cover.image_id,platforms.name,involved_companies.company.name,"
        + "involved_companies.developer,involved_companies.publisher";

    private readonly IgdbOptions _options = options.Value;

    public override bool Supports(WorkKind kind) => _options.IsConfigured && kind == WorkKind.Game;

    public override Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

    public override async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
    {
        if (!_options.IsConfigured || query.Kind is not (null or WorkKind.Game) || string.IsNullOrWhiteSpace(query.Title))
        {
            return [];
        }

        var limit = Math.Clamp(query.Limit, 1, 50);
        var body = $"search \"{query.Title.Replace("\\", " ", StringComparison.Ordinal).Replace("\"", " ", StringComparison.Ordinal)}\"; "
            + $"fields {Fields}; limit {limit};";

        var result = await FetchAsync(
            SearchKey("title", query.Title, limit),
            async token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUrl, "v4/games"))
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/plain"),
                };
                request.Headers.Add("Client-ID", _options.ClientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(token));
                return request;
            },
            ct,
            refreshAuth: tokens.RefreshAsync,
            isEmpty: doc => doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0);
        if (result.Json is null)
        {
            return [];
        }

        using var json = result.Json;
        var wantedPlatform = PlatformNames.Canonicalize(query.Platform);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // The platform read is a hint, never a filter: a game not on it is kept under its own platform, after the ones
        // that are, and the scorer weighs the difference. A box read as "Nintendo Switch" that is really a Switch 2 game
        // was dropped here and came back "not in the catalogue" every time.
        return json.RootElement.EnumerateArray()
            .Select(g => FromGame(g, wantedPlatform))
            .OfType<ProviderRelease>()
            .OrderBy(r => wantedPlatform is null || PlatformNames.Canonicalize(r.Platform) == wantedPlatform ? 0 : 1)
            .Take(query.Limit)
            .ToList();
    }

    private static ProviderRelease? FromGame(JsonElement game, string? wantedPlatform)
    {
        var id = game.Num("id");
        var title = game.Str("name");
        if (id is null || title is null)
        {
            return null;
        }

        var platforms = game.Items("platforms").Select(p => p.Str("name")).OfType<string>().ToList();
        var platform = (wantedPlatform is null ? null : platforms.FirstOrDefault(p => PlatformNames.Canonicalize(p) == wantedPlatform))
            ?? platforms.FirstOrDefault();
        var companies = game.Items("involved_companies").ToList();

        var externalId = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ProviderRelease
        {
            Provider = ProviderName,
            ExternalId = externalId,
            WorkKey = externalId, // an IGDB game entry is a work; its platforms are editions
            Title = title,
            Kind = WorkKind.Game,
            Platform = platform,
            Year = game.Num("first_release_date") is { } seconds
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).Year
                : null,
            Creators = companies.Where(c => c.Prop("developer") is { ValueKind: JsonValueKind.True })
                .Select(c => c.Prop("company")?.Str("name")).OfType<string>().ToList(),
            Publisher = companies.Where(c => c.Prop("publisher") is { ValueKind: JsonValueKind.True })
                .Select(c => c.Prop("company")?.Str("name")).OfType<string>().FirstOrDefault(),
            Synopsis = game.Str("summary"),
            CoverUrl = game.Prop("cover")?.Str("image_id") is { } image
                ? CoverHosts.Allow($"https://images.igdb.com/igdb/image/upload/t_cover_big/{Uri.EscapeDataString(image)}.jpg")
                : null,
        };
    }
}
