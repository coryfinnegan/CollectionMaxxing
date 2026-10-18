using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Catalog;

/// <summary>Base of the catalogue tests: a client for any user, with fake providers and a throwaway cover folder.</summary>
public abstract class CatalogTest(ApiFixture api) : ApiTest(api)
{
    protected string CoversRoot { get; } = Path.Combine(Path.GetTempPath(), "cm-covers-" + Guid.NewGuid().ToString("N"));

    public override async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(CoversRoot);
        await base.InitializeAsync();
    }

    public override ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(CoversRoot, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }

        GC.SuppressFinalize(this);
        return base.DisposeAsync();
    }

    /// <summary>A client signed in as <paramref name="userId"/> against an app whose only providers are <paramref name="providers"/>.</summary>
    protected HttpClient ClientWith(Guid? userId, params ICatalogProvider[] providers)
    {
        var factory = Api.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Catalog:CoversRoot"] = CoversRoot }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new CatalogOptions { ProviderTimeout = TimeSpan.FromMilliseconds(300) });
                services.RemoveAll<ICatalogProvider>();
                foreach (var provider in providers)
                {
                    services.AddSingleton(provider);
                }
            });
        });

        // Never follow a redirect: assertions must see the first answer (NoRedirectTests relies on it).
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, id.ToString());
        }

        return client;
    }

    protected static async Task<string[]> TitlesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("work").GetProperty("title").GetString()!).ToArray();
    }
}

/// <summary>A provider that answers from a list, or fails on purpose.</summary>
internal sealed class FakeProvider(string name = "fake") : ICatalogProvider
{
    public List<ProviderRelease> Results { get; } = [];

    public ProviderRelease? LookupResult { get; set; }

    public Func<CancellationToken, Task>? Behaviour { get; set; }

    public string Name => name;

    public bool Supports(WorkKind kind) => true;

    public async Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct)
    {
        if (Behaviour is not null)
        {
            await Behaviour(ct);
        }

        return LookupResult;
    }

    public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
    {
        if (Behaviour is not null)
        {
            await Behaviour(ct);
        }

        return Results;
    }
}
