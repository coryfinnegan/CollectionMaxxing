using CollectionMaxxing.Api.Auth.KeyRing;
using CollectionMaxxing.Application.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Validation.AspNetCore;

namespace CollectionMaxxing.Api.Auth;

public static class AuthSetup
{
    /// <summary>The scheme that picks between the two ways in: the session cookie (web) or a bearer token (iPhone).</summary>
    public const string SchemeName = "CookieOrBearer";

    /// <summary>Whether the caller presented a bearer token (the iPhone) rather than leaving it to the session cookie (the web).</summary>
    public static bool IsBearerCall(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddApiAuth(this IServiceCollection services)
    {
        // Who is calling: one object per request, filled in by CurrentUserMiddleware.
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<CurrentUser>());

        // Two ways in: the __Host-cm session cookie (web, same origin) or an OpenIddict bearer token (iPhone). An Authorization
        // header means a bearer call; anything else is judged by its cookie.
        services.AddAuthentication(SchemeName)
            .AddPolicyScheme(SchemeName, "Session cookie or bearer token", options =>
                options.ForwardDefaultSelector = context =>
                    IsBearerCall(context.Request) ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme : AuthCookie.Scheme)
            .AddCookie(AuthCookie.Scheme, AuthCookie.Configure);
        services.AddOptions<CookieAuthenticationOptions>(AuthCookie.Scheme).Configure<TimeProvider>((cookie, time) => cookie.TimeProvider = time);

        // Deny by default: an endpoint with no authorization metadata still needs a signed-in user.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Admin, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Admin));
        services.AddSingleton<IAuthorizationHandler, LibraryRoleHandler>();

        // The cookie and the passkey ceremony are sealed with Data Protection keys kept in a configured folder (Auth:KeyRing:Path).
        services.AddPersistedKeyRing();

        services.AddTokenServer();
        services.AddAuthRateLimiting();
        return services;
    }

    /// <summary>
    /// The security part of the pipeline, in order: the CSRF header check (cheap, so first), the rate limiter (it needs the
    /// matched endpoint), who is calling, the caller's libraries, and then authorization.
    /// </summary>
    public static IApplicationBuilder UseApiAuth(this IApplicationBuilder app) =>
        app.UseMiddleware<CsrfMiddleware>()
            .UseRateLimiter()
            .UseAuthentication()
            .UseMiddleware<CurrentUserMiddleware>()
            .UseAuthorization();
}
