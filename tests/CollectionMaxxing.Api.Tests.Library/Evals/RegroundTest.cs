using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// Base of the tests of re-grounding: a run is taken on one server version (<see cref="OldSha"/>), the API is stopped, and a second host over the same
/// folders starts as another version (<see cref="NewSha"/>), the way a redeploy does. The worker of the second host decides, by itself, whether to
/// ground and score the first run's readings again.
/// </summary>
public abstract class RegroundTest : OpenRunTest
{
    protected const string OldSha = "aaaa111";
    protected const string NewSha = "bbbb222";

    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    protected RegroundTest(ApiFixture api)
        : base(api)
    {
        Settings["Evals:BuildSha"] = OldSha;
    }

    /// <summary>A phone run of the given photos (each read as <paramref name="read"/>, Star Fox 64 unless told), opened, finished and scored by the first host. Returns the run id.</summary>
    protected static async Task<string> PhoneRunAsync(HttpClient client, string[] photoIds, bool partial = false, Func<object>? read = null)
    {
        var runId = (await OpenOkAsync(client)).GetProperty("runId").GetString()!;
        foreach (var id in photoIds)
        {
            await PutPhotoOkAsync(client, id, AllEngines(id, read ?? StarFoxRead));
        }

        await FinishOkAsync(client, partial);
        await StatusWhenDoneAsync(client);
        return runId;
    }

    /// <summary>Stops the first host and starts a second one, over the same folders and database, as <paramref name="sha"/>.</summary>
    protected async Task<WebApplicationFactory<Program>> RedeployAsync(string sha = NewSha, Action<Dictionary<string, string?>>? change = null)
    {
        await Factory.DisposeAsync();
        Settings["Evals:BuildSha"] = sha;
        change?.Invoke(Settings);
        var host = Api.Factory.WithWebHostBuilder(builder =>
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
                services.RemoveAll<ICatalogProvider>();
                services.AddSingleton<ICatalogProvider>(Games);
                services.AddSingleton<ICatalogProvider>(Books);
                services.RemoveAll<IProductLookup>();
                services.AddSingleton<IProductLookup>(Products);
                ConfigureServices?.Invoke(services);
            });
        });
        _ = host.Server; // starts the host, and with it the worker
        _hosts.Add(host);
        return host;
    }

    public override async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    protected static void Wake(WebApplicationFactory<Program> host) => host.Services.GetRequiredService<EvalRunCoordinator>().Wake();

    /// <summary>Runs the app on a clock the test moves, so a wait between tries of a failed photo is advanced by hand instead of waited out for real.</summary>
    protected void UseClock(FakeTimeProvider clock) =>
        AddConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    /// <summary>
    /// Runs the app over a local catalogue search that refuses to answer while <paramref name="fails"/> says so: a lookup that fails under the PC
    /// (a provider's own failure is never let through, by design (<c>docs/02</c>), so this is the fault a real "the lookup failed" looks like).
    /// </summary>
    protected void UseFlakyLocalSearch(Func<bool> fails) =>
        AddConfigureServices(services =>
        {
            var original = services.Single(d => d.ServiceType == typeof(ILocalCatalogSearch));
            services.Remove(original);
            services.AddScoped<ILocalCatalogSearch>(provider =>
                new FlakyLocalSearch((ILocalCatalogSearch)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!), fails));
        });

    protected static HttpClient AdminOn(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "admin");
        return client;
    }

    /// <summary>Reads a file the way that never stops the server from replacing it: a test that polls a result file must not make the server's own write fail.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    protected JsonElement? TryLatestSummary()
    {
        try
        {
            return JsonDocument.Parse(ReadShared(Path.Combine(Golden.Results, "latest.json"))).RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null; // caught between two writes, or not there yet: look again
        }
    }

    protected string? LatestRunId() => TryLatestSummary() is { } summary && summary.TryGetProperty("runId", out var id) ? id.GetString() : null;

    protected string? LatestSha() => TryLatestSummary() is { } summary && summary.TryGetProperty("buildSha", out var sha) ? sha.GetString() : null;

    /// <summary>Waits for the result of another run than <paramref name="runId"/> to be the latest, and returns its summary.</summary>
    protected async Task<JsonElement> NextLatestAfterAsync(string runId)
    {
        (await BecomesTrueAsync(() => LatestRunId() is { } id && id != runId, Bounded))
            .ShouldBeTrue($"no other result was written; the re-ground said: {string.Join(" | ", Logs.Lines.Where(l => l.Contains("Eval re-ground", StringComparison.Ordinal)))}");
        return TryLatestSummary()!.Value;
    }

    protected Task<JsonElement> KeptInputsAsync() =>
        Task.FromResult(JsonDocument.Parse(ReadShared(Path.Combine(Golden.Results, "latest-inputs.json"))).RootElement.Clone());

    protected Task<bool> LogsAsync(string fragment) =>
        BecomesTrueAsync(() => Logs.Lines.Any(line => line.Contains(fragment, StringComparison.Ordinal)), Bounded);

    protected bool Logged(string fragment) => Logs.Lines.Any(line => line.Contains(fragment, StringComparison.Ordinal));

    protected static Dictionary<string, double> MetricsOf(JsonElement summary) =>
        summary.GetProperty("metrics").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);

    /// <summary>True when the summary says it was scored again from an earlier run's readings.</summary>
    protected static bool IsRegrounded(JsonElement summary) =>
        summary.TryGetProperty("regroundedFrom", out var from) && from.ValueKind == JsonValueKind.Object;
}
