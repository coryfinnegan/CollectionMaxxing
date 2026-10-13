using CollectionMaxxing.Application.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace CollectionMaxxing.Api.Auth;

public static class AuthSetup
{
    public static IServiceCollection AddApiAuth(this IServiceCollection services)
    {
        // Who is calling: one object per request, filled in by CurrentUserMiddleware.
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<CurrentUser>());

        services.AddAuthentication(NoSignInHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, NoSignInHandler>(NoSignInHandler.SchemeName, _ => { });

        // Deny by default: an endpoint with no authorization metadata still needs a signed-in user.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Admin, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Admin));
        services.AddSingleton<IAuthorizationHandler, LibraryRoleHandler>();
        return services;
    }

    public static IApplicationBuilder UseApiAuth(this IApplicationBuilder app) =>
        app.UseAuthentication()
            .UseMiddleware<CurrentUserMiddleware>()
            .UseAuthorization();
}
