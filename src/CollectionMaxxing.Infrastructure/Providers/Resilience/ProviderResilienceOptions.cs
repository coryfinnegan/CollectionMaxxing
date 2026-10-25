using CollectionMaxxing.Application.Catalog.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Providers.Resilience;

/// <summary>The resilience pipeline every provider client shares (<c>Providers:Resilience</c>). Defaults follow docs/02.</summary>
public sealed class ProviderResilienceOptions
{
    public const string SectionName = "Providers:Resilience";

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}

internal static class ProviderResilienceExtensions
{
    /// <summary>Adds the standard pipeline: per-attempt timeout, jittered retries honouring Retry-After, circuit breaker.</summary>
    public static IHttpClientBuilder AddProviderResilience(this IHttpClientBuilder builder)
    {
        // The app gives every HttpClient the standard handler (ServiceDefaults); left in place it wraps this one with default, unconfigured
        // retries (3, 2 s apart, doubling) that outlast the caller's time budget. A provider client has exactly one pipeline: ours.
#pragma warning disable EXTEXP0001 // Experimental only in name; it is the supported way to drop the app-wide default.
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        // The default request logging prints the whole URL path. Open Library's ISBN lookup carries the code in the path, and a
        // scanned code never goes to the log (docs/08), so a provider client logs nothing of its own; the adapters log by name.
        builder.RemoveAllLoggers();
        builder.AddStandardResilienceHandler();
        builder.Services.AddOptions<HttpStandardResilienceOptions>(builder.Name + "-standard")
            .Configure<IOptions<ProviderResilienceOptions>>((options, shared) =>
            {
                var settings = shared.Value;
                var sampling = settings.AttemptTimeout * 2;
                options.AttemptTimeout.Timeout = settings.AttemptTimeout;
                options.TotalRequestTimeout.Timeout = settings.TotalTimeout;
                options.CircuitBreaker.SamplingDuration = sampling > TimeSpan.FromSeconds(30) ? sampling : TimeSpan.FromSeconds(30);
                options.Retry.MaxRetryAttempts = settings.MaxRetryAttempts;
                options.Retry.Delay = settings.RetryDelay;
                options.Retry.UseJitter = true;
                options.Retry.ShouldRetryAfterHeader = true;

                // Someone is waiting on this answer (a scan or a search): retrying a failure means sleeping through back-off until
                // their time budget runs out, so a failure ends the call at once. The background job keeps the retries.
                var transient = options.Retry.ShouldHandle;
                options.Retry.ShouldHandle = args =>
                {
                    if (args.Outcome.Exception is ProviderQuotaExhaustedException)
                    {
                        return ValueTask.FromResult(false); // the day's quota is spent: a retry would send a request that was already refused
                    }

                    return ProviderCalls.IsFailFast ? ValueTask.FromResult(false) : transient(args);
                };
            });
        return builder;
    }
}
