using CollectionMaxxing.Application.Catalog.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Providers.Resilience;

/// <summary>The resilience pipeline every provider client shares (<c>Providers:Resilience</c>). Defaults follow docs/02.</summary>
public sealed class ProviderResilienceOptions
{
    public const string SectionName = "Providers:Resilience";

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}

internal static class ProviderResilienceExtensions
{
    /// <summary>Adds the standard pipeline: per-attempt timeout, jittered retries honouring Retry-After, circuit breaker.</summary>
    public static IHttpClientBuilder AddProviderResilience(this IHttpClientBuilder builder)
    {
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
                options.Retry.ShouldHandle = args => ProviderCalls.IsFailFast ? ValueTask.FromResult(false) : transient(args);
            });
        return builder;
    }
}
