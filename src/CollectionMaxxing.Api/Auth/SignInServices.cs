using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth;

/// <summary>The services behind the sign-in endpoints (kept out of the endpoint class, which never touches Infrastructure).</summary>
internal static class SignInServices
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddIdentityAccounts();

        // Passkeys: Identity's handler verifies what the browser sends. The relying party is the host people browse to.
        services.AddScoped<IPasskeyHandler<AppUser>, PasskeyHandler<AppUser>>();
        services.AddOptions<IdentityPasskeyOptions>().Configure<IOptions<AuthOptions>>((passkey, auth) =>
        {
            passkey.ServerDomain = auth.Value.RelyingPartyId;
            passkey.ValidateOrigin = context => ValueTask.FromResult(IsOurOrigin(context.Origin, auth.Value.RelyingPartyId));
        });

        services.AddSingleton<CeremonyCookie>();
        services.AddScoped<PasskeyCeremonies>();
        services.AddScoped<SpaPage>();
        services.AddScoped<IssueCode.Handler>();
        services.AddScoped<VerifyCode.Handler>();
        services.AddScoped<PlanCeremony.Handler>();
        services.AddScoped<CompleteCeremony.Handler>();
        services.AddScoped<GetSetupStatus.Handler>();
        services.AddScoped<RemovePasskey.Handler>();
        services.AddHostedService<SetupCodeBootstrapper>();

        // A setup code, an invite token or a credential may be in a URL or a body; the framework's request log would print the URL.
        // The router's debug lines print the path too.
        services.AddLogging(logging => logging
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Routing.Matching", LogLevel.Information));
        return services;
    }

    /// <summary>
    /// A passkey may only be made and used on the server's own origin: https on the relying-party host (or, for local
    /// development, any loopback address on that host name).
    /// </summary>
    internal static bool IsOurOrigin(string? origin, string relyingPartyId)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, relyingPartyId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }
}
