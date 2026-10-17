using System.Collections.Concurrent;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Domain.Accounts;
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
/// The real API with REAL sign-in (passkeys, the <c>__Host-cm</c> cookie), unlike <c>TestApiFactory</c> which signs in from a
/// header. It runs on a clock the test controls and records every log line, so tests can prove what never gets logged.
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
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:collectionmaxxing"] = connectionString,
                    ["Auth:RelyingPartyId"] = "localhost",
                    ["Auth:SecretsPath"] = _secretsPath,
                    ["Auth:Setup:IssueOnStartup"] = "false",
                    ["Auth:RateLimit:PermitLimit"] = "10000",
                };
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

                configure?.Invoke(services);
            });
        });
    }

    public string SecretsPath => _secretsPath;

    public FakeTimeProvider Time { get; }

    public LogCapture Logs { get; } = new();

    public IServiceProvider Services => _factory.Services;

    /// <summary>A browser: its own cookie jar and its own passkey.</summary>
    public WebClient NewBrowser() => new(_factory.CreateClient());

    /// <summary>Issues a one-time code the way the server's start-up and its command line do.</summary>
    public async Task<string> IssueCodeAsync(SetupCodePurpose purpose)
    {
        using var scope = Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<IssueCode.Handler>().HandleAsync(purpose, CancellationToken.None);
        return issued?.Code ?? throw new InvalidOperationException($"No {purpose} code was issued.");
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
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
