using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// What every service in the solution gets: OpenTelemetry, health checks and HTTP resilience.
/// Call <see cref="AddServiceDefaults{TBuilder}"/> while building and <see cref="MapDefaultEndpoints"/> after.
/// </summary>
public static class Extensions
{
    public const string LivenessPath = "/health/live";
    public const string ReadinessPath = "/health/ready";
    public const string VersionPath = "/health/version";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        // Every HttpClient gets retries with jitter, timeouts and a circuit breaker.
        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(options =>
                    // Health probes would drown out real traces.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                .AddHttpClientInstrumentation());

        // Export only when something is listening (the Aspire dashboard sets this variable).
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // "live": the process is up. Checks tagged "ready" (the database, for one) are added by the features that need them.
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    /// <summary>
    /// Maps <c>/health/live</c>, <c>/health/ready</c> and <c>/health/version</c>. They answer anonymous callers
    /// on purpose, and all three stay out of the OpenAPI document.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(LivenessPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") })
            .AllowAnonymous();
        app.MapHealthChecks(ReadinessPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
            .AllowAnonymous();
        // #491: the commit this build came from, so a merge's delivery can be verified against the running
        // server. MapHealthChecks stays out of the OpenAPI document by itself; a MapGet needs to be told.
        app.MapGet(VersionPath, (IConfiguration config) => TypedResults.Ok(BuildVersion.From(config["CM_BUILD_SHA"])))
            .ExcludeFromDescription()
            .AllowAnonymous();
        return app;
    }

    /// <summary>
    /// The commit the running build came from (the <c>CM_BUILD_SHA</c> the compose file passes in), or
    /// <c>"unknown"</c> when it was not set or is not a commit hash.
    /// </summary>
    public sealed record BuildVersion(string Sha, string ShortSha)
    {
        public const string Unknown = "unknown";

        public static BuildVersion From(string? sha)
        {
            var clean = sha?.Trim();
            if (clean is not { Length: 40 } || clean.Any(c => !Uri.IsHexDigit(c)))
            {
                return new BuildVersion(Unknown, Unknown);
            }

            return new BuildVersion(clean, clean[..12]);
        }
    }
}
