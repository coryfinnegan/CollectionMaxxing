using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure;
using CollectionMaxxing.Infrastructure.Providers;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using WireMock.Server;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>
/// The five provider adapters wired the way the app wires them, but pointed at a WireMock server and the test Postgres.
/// No test ever reaches a real provider. Every provider is configured (fake keys) and effectively unthrottled, unless a
/// test overrides a setting.
/// </summary>
internal sealed class ProviderHost : IDisposable
{
    public const string ValidIsbn = "9780140328721";

    private ProviderHost(WireMockServer server, ServiceProvider services, TimeProvider time)
    {
        Server = server;
        Services = services;
        Time = time;
    }

    public WireMockServer Server { get; }

    public ServiceProvider Services { get; }

    public TimeProvider Time { get; }

    public FakeTimeProvider Fake => (FakeTimeProvider)Time;

    /// <param name="fakeTime">Use a fake clock (cache expiry, quota days). Retries and timeouts need the real one.</param>
    /// <param name="settings">Configuration overrides, e.g. <c>["Providers:Tmdb:ApiKey"] = null</c>.</param>
    public static ProviderHost Create(PostgresFixture db, bool fakeTime = false, Dictionary<string, string?>? settings = null)
    {
        var server = WireMockServer.Start();
        var baseUrl = server.Url!.TrimEnd('/') + "/";

        var values = new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DependencyInjection.ConnectionStringName}"] = db.ConnectionString,
            ["Providers:Resilience:AttemptTimeout"] = "00:00:05",
            ["Providers:Resilience:MaxRetryAttempts"] = "2",
            ["Providers:Resilience:RetryDelay"] = "00:00:00.100",
            ["Providers:OpenLibrary:BaseUrl"] = baseUrl,
            ["Providers:OpenLibrary:ContactEmail"] = "owner@example.test",
            ["Providers:OpenLibrary:RequestsPerSecond"] = "1000000000",
            ["Providers:GoogleBooks:BaseUrl"] = baseUrl,
            ["Providers:GoogleBooks:ApiKey"] = "google-test-key",
            ["Providers:GoogleBooks:RequestsPerSecond"] = "1000000000",
            ["Providers:UpcItemDb:BaseUrl"] = baseUrl,
            ["Providers:UpcItemDb:RequestsPerSecond"] = "1000000000",
            ["Providers:Tmdb:BaseUrl"] = baseUrl,
            ["Providers:Tmdb:ApiKey"] = "tmdb-test-key",
            ["Providers:Tmdb:RequestsPerSecond"] = "1000000000",
            ["Providers:Igdb:BaseUrl"] = baseUrl,
            ["Providers:Igdb:TokenUrl"] = baseUrl + "oauth2/token",
            ["Providers:Igdb:ClientId"] = "twitch-test-id",
            ["Providers:Igdb:ClientSecret"] = "twitch-test-secret",
            ["Providers:Igdb:RequestsPerSecond"] = "1000000000",
        };
        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        TimeProvider time = fakeTime ? new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)) : TimeProvider.System;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSingleton(time);
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
        services.AddInfrastructure();
        services.AddCatalogProviderAdapters();
        return new ProviderHost(server, services.BuildServiceProvider(), time);
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Requests the mock saw whose path ends with <paramref name="path"/>.</summary>
    public int Requests(string path) =>
        Server.LogEntries.Count(e => e.RequestMessage?.Path?.EndsWith(path, StringComparison.Ordinal) == true);

    /// <summary>Waits until the mock has logged that many requests (it logs a request when its response is finished).</summary>
    public async Task<int> WaitForRequestsAsync(string path, int expected)
    {
        for (var i = 0; i < 100 && Requests(path) < expected; i++)
        {
            await Task.Delay(50);
        }

        return Requests(path);
    }

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Providers", name));

    public void Dispose()
    {
        Services.Dispose();
        Server.Stop();
        Server.Dispose();
    }
}
