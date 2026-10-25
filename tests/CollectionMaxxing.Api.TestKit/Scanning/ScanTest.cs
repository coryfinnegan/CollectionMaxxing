using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Api.Tests.Scanning;

/// <summary>A product lookup that answers from a table, or does whatever a test tells it to.</summary>
public sealed class FakeProductLookup : IProductLookup
{
    private readonly Dictionary<string, ProductLookupResult> _answers = [];

    public Func<Identifier, CancellationToken, Task<ProductLookupResult>>? Behaviour { get; set; }

    public int Calls { get; private set; }

    public void Know(string code, ProviderRelease release)
    {
        Identifier.TryParse(code, out var id).ShouldBeTrue();
        _answers[id!.Value] = ProductLookupResult.Found(release);
    }

    public async Task<ProductLookupResult> LookupAsync(Identifier id, CancellationToken cancellationToken)
    {
        Calls++;
        if (Behaviour is not null)
        {
            return await Behaviour(id, cancellationToken);
        }

        return _answers.TryGetValue(id.Value, out var answer) ? answer : ProductLookupResult.NotFound;
    }
}

/// <summary>Codes and provider releases the scanning tests share.</summary>
public static class ScanData
{
    /// <summary>A valid UPC-A for any number (the leading digits are the number, then the check digit).</summary>
    public static string Upc(int n)
    {
        var body = $"0{n:D10}";
        var sum = body.Select((c, i) => (c - '0') * (i % 2 == 0 ? 3 : 1)).Sum();
        return body + ((10 - (sum % 10)) % 10);
    }

    /// <summary>The 13-digit form of <see cref="Upc"/>: what a candidate's rawCode reads.</summary>
    public static string Gtin13(string upc)
    {
        Identifier.TryParse(upc, out var id).ShouldBeTrue();
        return id!.Value;
    }

    public const string Isbn = "9780441172719";

    public static ProviderRelease Product(string title, string code, string platform = "Nintendo 64", int year = 1996) => new()
    {
        Provider = "upcitemdb",
        ExternalId = code,
        Title = title,
        Kind = WorkKind.Game,
        Format = ReleaseFormat.GameCartridge,
        Platform = platform,
        Year = year,
        Identifiers = Identifier.TryParse(code, out var id) ? [id!] : [],
    };

    public static ProviderRelease Game(string title, string platform = "Nintendo 64", int year = 1996, string externalId = "g1") => new()
    {
        Provider = "igdb",
        ExternalId = externalId,
        Title = title,
        Kind = WorkKind.Game,
        Format = ReleaseFormat.GameCartridge,
        Platform = platform,
        Year = year,
    };
}

/// <summary>Base of the scanning tests: the real app with fake providers and a fake product lookup, and helpers for the scan endpoints.</summary>
public abstract class ScanTest(ApiFixture api) : ApiTest(api)
{
    private WebApplicationFactory<Program>? _factory;

    public FakeProductLookup Products { get; } = new();

    public FakeProvider Games { get; } = new("igdb");

    public FakeProvider Books { get; } = new("openlibrary");

    // A budget that can't expire however busy the PC is (a slow lookup would otherwise leave a code pending). Tests about the budget expiring set a short one themselves.
    protected ScanningOptions Options { get; } = new() { ResolveBudget = TimeSpan.FromMinutes(2) };

    /// <summary>Extra configuration for the app; set before the first client is made.</summary>
    protected Dictionary<string, string?> Settings { get; } = [];

    /// <summary>Extra services for the app; set before the first client is made.</summary>
    protected Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>Every line the app logs while the test runs.</summary>
    protected LogCapture Logs { get; } = new();

    /// <summary>True keeps the app's real provider adapters (point them somewhere harmless with <see cref="Settings"/>).</summary>
    protected bool UseRealProviders { get; set; }

    protected WebApplicationFactory<Program> Factory => _factory ??= Api.Factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Options);
            if (!UseRealProviders)
            {
                services.RemoveAll<ICatalogProvider>();
                services.AddSingleton<ICatalogProvider>(Games);
                services.AddSingleton<ICatalogProvider>(Books);
                services.RemoveAll<IProductLookup>();
                services.AddSingleton<IProductLookup>(Products);
            }

            ConfigureServices?.Invoke(services);
        });
    });

    public override async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        GC.SuppressFinalize(this);
        await base.DisposeAsync();
    }

    protected HttpClient AsUser(Guid userId)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        return client;
    }

    protected HttpClient As(TestLibrary library, LibraryRole role) => AsUser(library.UserWithRole(role));

    protected static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    protected static string SessionsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/scan-sessions";

    protected static string SessionUrl(Guid sessionId) => $"/api/v1/scan-sessions/{sessionId}";

    protected static string CandidateUrl(Guid sessionId, Guid candidateId) => $"{SessionUrl(sessionId)}/candidates/{candidateId}";

    /// <summary>Starts a session as <paramref name="client"/>'s user and returns its id.</summary>
    protected static async Task<Guid> StartSessionAsync(HttpClient client, Guid libraryId, object? body = null)
    {
        var response = await client.PostAsync(SessionsUrl(libraryId), Json(body ?? new { }));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    protected static object Code(string value, int? copies = null) => new { value, symbology = "ean_13", copies };

    protected static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid sessionId, string key, params object[] codes)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/barcodes") { Content = Json(new { codes }) };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    /// <summary>Submits codes and returns the candidates the response carries (it must be a 200).</summary>
    protected static async Task<List<JsonElement>> ScanAsync(HttpClient client, Guid sessionId, params string[] codes)
    {
        var response = await SubmitAsync(client, sessionId, Guid.NewGuid().ToString(), codes.Select(c => Code(c)).ToArray());
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    protected static Task<HttpResponseMessage> CommitAsync(HttpClient client, Guid sessionId, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/commit");
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    protected static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid sessionId, Guid candidateId, object body) =>
        client.PatchAsync(CandidateUrl(sessionId, candidateId), Json(body));

    protected static async Task<JsonElement> GetSessionAsync(HttpClient client, Guid sessionId, int? since = null)
    {
        var response = await client.GetAsync(SessionUrl(sessionId) + (since is null ? string.Empty : $"?since={since}"));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadJsonAsync(response);
    }

    protected async Task<int> CountItemsAsync(Guid libraryId)
    {
        await using var context = Db.CreateContext();
        return await context.Items.IgnoreQueryFilters().CountAsync(i => i.LibraryId == libraryId);
    }

    /// <summary>A release the library owns one copy of, found by <paramref name="code"/> (a UPC-A or EAN-13).</summary>
    protected async Task<(Guid ReleaseId, Guid ItemId)> OwnAsync(
        TestLibrary library, string title, string? code, ReleaseFormat format = ReleaseFormat.GameCartridge, WorkKind kind = WorkKind.Game,
        int? year = 1996, Guid? platformId = null, Guid? locationId = null)
    {
        Identifier? id = null;
        if (code is not null)
        {
            Identifier.TryParse(code, out id).ShouldBeTrue();
        }

        var (_, releaseId) = await CatalogSeed.AddAsync(
            Db, title, kind, year, platformId, barcode: id?.Value, barcodeType: id?.Type ?? IdentifierType.Gtin13, format: format);
        var itemId = await TestData.CreateItemAsync(Db, library, releaseId, locationId);
        return (releaseId, itemId);
    }

    /// <summary>Adds a release to the catalogue without any owner, found by <paramref name="code"/>.</summary>
    protected async Task<Guid> CatalogAsync(
        string title, string code, ReleaseFormat format = ReleaseFormat.GameCartridge, WorkKind kind = WorkKind.Game, int? year = 1996)
    {
        Identifier.TryParse(code, out var id).ShouldBeTrue();
        var (_, releaseId) = await CatalogSeed.AddAsync(Db, title, kind, year, barcode: id!.Value, barcodeType: id.Type, format: format);
        return releaseId;
    }

    protected static string Resolution(JsonElement candidate) => candidate.GetProperty("resolution").GetString()!;

    protected static string Decision(JsonElement candidate) => candidate.GetProperty("decision").GetString()!;

    protected static Guid Id(JsonElement candidate) => candidate.GetProperty("id").GetGuid();
}

/// <summary>Reads a few of the items' fields the scanning tests care about.</summary>
public static class ScanReaders
{
    public static async Task<List<Item>> ItemsAsync(PostgresFixture db, Guid libraryId)
    {
        await using var context = db.CreateContext();
        return await context.Items.IgnoreQueryFilters().AsNoTracking().Where(i => i.LibraryId == libraryId).OrderBy(i => i.CreatedAt).ToListAsync();
    }
}
