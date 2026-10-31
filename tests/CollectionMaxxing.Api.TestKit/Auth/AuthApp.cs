using System.Collections.Concurrent;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>
/// The real API with REAL sign-in (a fake Auth0 on loopback, the <c>__Host-cm</c> cookie, Auth0 bearer tokens), unlike
/// <c>TestApiFactory</c> which signs in from a header. It runs on a clock the test controls and records every log line, so tests can
/// prove what never gets logged.
/// </summary>
public sealed class AuthApp : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _secretsPath = Path.Combine(Path.GetTempPath(), "cm-tests-" + Guid.NewGuid().ToString("N"));

    public AuthApp(string connectionString, Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null)
    {
        Time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var time = Time;
        var logs = Logs;
        var auth0 = Auth0;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:collectionmaxxing"] = connectionString,
                    ["Auth:SecretsPath"] = _secretsPath,

                    // Not the app's default folders beside the test binaries: test classes run in parallel and would share them.
                    ["Catalog:CoversRoot"] = Path.Combine(_secretsPath, "covers"),
                    ["Exports:Path"] = Path.Combine(_secretsPath, "exports"),
                    ["Auth:RateLimit:PermitLimit"] = "10000",
                    ["Auth:RateLimit:AccountPermitLimit"] = "10000",
                    ["Auth:RateLimit:NewAccountsPerHour"] = "10000",
                    ["Security:Limits:Export:Permits"] = "100000",
                    ["Security:Limits:AuthHourly:Permits"] = "100000",
                };
                foreach (var (key, value) in FakeAuth0.Settings)
                {
                    values[key] = value;
                }

                foreach (var (key, value) in settings ?? [])
                {
                    values[key] = value;
                }

                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(logs);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);

                // No background worker: jobs run only when a test runs them with JobRunner, so no test depends on worker timing.
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(JobWorker)).ToList())
                {
                    services.Remove(worker);
                }

                auth0.Configure(services);
                configure?.Invoke(services);
            });
        });
    }

    public string SecretsPath => _secretsPath;

    public FakeTimeProvider Time { get; }

    public LogCapture Logs { get; } = new();

    public IServiceProvider Services => _factory.Services;

    /// <summary>The fake Auth0 tenant this app trusts.</summary>
    public FakeAuth0 Auth0 { get; } = new();

    /// <summary>A browser: its own cookie jar, and its own Google identity unless one is given.</summary>
    public WebClient NewBrowser(TestIdentity? identity = null) =>
        new(_factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), Auth0, Time) { Identity = identity ?? TestIdentity.Google() };

    /// <summary>A phone signed in to Auth0 as <paramref name="subject"/>: every request carries its access token.</summary>
    public HttpClient NewPhone(string subject) => NewPhoneWithToken(Auth0.AccessToken(subject));

    /// <summary>A client that sends <paramref name="accessToken"/> as its bearer token.</summary>
    public HttpClient NewPhoneWithToken(string accessToken)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Issues a recovery code for the account with <paramref name="email"/>, the way <c>cm-admin recovery-code</c> does.</summary>
    public async Task<string> IssueRecoveryCodeAsync(string email)
    {
        using var scope = Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<IssueCode.Handler>().HandleAsync(email, CancellationToken.None);
        return issued.IsSuccess ? issued.Value.Code : throw new InvalidOperationException($"No recovery code was issued: {issued.Error!.Code}.");
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        Auth0.Dispose();
        if (Directory.Exists(_secretsPath))
        {
            Directory.Delete(_secretsPath, recursive: true);
        }
    }
}

/// <summary>Collects every log line the app writes (message, exception and category).</summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}
