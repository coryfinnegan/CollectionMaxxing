using CollectionMaxxing.Api.Auth;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Security;

/// <summary>
/// The response headers every answer carries (docs/08, "HTTP hardening"), the settings that bound a request (size, time) and the rate
/// limits. <see cref="AddSecurityHeaders"/> registers them; <c>app.UseSecurityHeaders()</c> goes first in the pipeline so that even an error,
/// a 404 and a 429 have the headers, and <c>app.UseApiLimits()</c> goes after <c>UseApiFoundation()</c> (it needs to know who is calling).
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// The web app's Content-Security-Policy, enforced. docs/08's policy plus <c>style-src 'unsafe-inline'</c>: react-native-web writes its style
    /// rules and per-element styles at run time, which the browser treats as inline style. Scripts get no exception: there is no
    /// <c>script-src</c> beyond <c>default-src 'self'</c>, so no inline script and no <c>eval</c>. (app/playwright/serve.mjs sends the same
    /// text; a test keeps the two equal.)
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'sha256-67fhrP0+BkBqmgGGXTtgiVO/9EQs3QruYNU/7fnRkI8='; img-src 'self' blob: data:; connect-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public const string StrictTransportSecurity = "max-age=31536000";

    public const string PermissionsPolicy = "camera=(self), geolocation=()";

    public static IServiceCollection AddSecurityHeaders(this IServiceCollection services)
    {
        services.AddOptions<SecurityOptions>()
            .BindConfiguration(SecurityOptions.SectionName)
            .Validate(options => options.Limits.All.All(l => l.Limit.Permits >= 1 && l.Limit.WindowSeconds >= 1),
                "Security:Limits:* need Permits and WindowSeconds of at least 1.")
            .Validate(options => options.RequestTimeoutSeconds >= 1, "Security:RequestTimeoutSeconds must be at least 1.")
            .Validate(options => options.MaxRequestBodyBytes >= 1024, "Security:MaxRequestBodyBytes must be at least 1024.")
            .ValidateOnStart();

        // The framework logs the full address of every request (query string included) and, below Information, every matched path.
        // Secrets travel there (signed-link tokens, invite tokens), so those lines are off; RequestLogMiddleware replaces them.
        services.AddLogging(logging => logging
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Warning));

        services.AddSingleton<ApiLimiter>();
        services.AddRequestTimeouts();
        services.AddOptions<RequestTimeoutOptions>().Configure<IOptions<SecurityOptions>>((timeouts, security) =>
            timeouts.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(security.Value.RequestTimeoutSeconds) });
        services.AddOptions<KestrelServerOptions>().Configure<IOptions<SecurityOptions>>((kestrel, security) =>
        {
            kestrel.Limits.MaxRequestBodySize = security.Value.MaxRequestBodyBytes;
            kestrel.AddServerHeader = false;
        });
        return services;
    }

    /// <summary>Adds the headers to every response, however it ends (success, error, 404, 429, a file).</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestLogMiddleware>().Use(async (context, next) =>
        {
            var hsts = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.Hsts
                ?? !context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                if (hsts)
                {
                    headers.StrictTransportSecurity = StrictTransportSecurity;
                }

                headers.XContentTypeOptions = "nosniff";
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = PermissionsPolicy;
                headers["Cross-Origin-Opener-Policy"] = "same-origin";

                // Nothing the API says about a person's collection is kept by a browser or a proxy. (Media endpoints set their own policy.)
                if (IsApi(context.Request.Path) && !headers.ContainsKey("Cache-Control"))
                {
                    headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            });
            await next(context);
        });

    private static bool IsApi(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/connect") || path.StartsWithSegments("/account");

    /// <summary>The rate limits and the request timeout. Goes right after <c>UseApiFoundation()</c>: the limiter needs the signed-in user, and anonymous callers are judged by address.</summary>
    public static IApplicationBuilder UseApiLimits(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiLimitsMiddleware>()
            .UseRequestTimeouts()
            .Use(async (context, next) =>
            {
                // A live event stream is meant to stay open.
                if (context.Request.Path.Value?.EndsWith("/events", StringComparison.Ordinal) == true)
                {
                    context.Features.Get<IHttpRequestTimeoutFeature>()?.DisableTimeout();
                }

                await next(context);
            });
}
