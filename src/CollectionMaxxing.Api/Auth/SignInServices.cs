using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Infrastructure.Identity;

namespace CollectionMaxxing.Api.Auth;

/// <summary>The services behind the sign-in endpoints (kept out of the endpoint class, which never touches Infrastructure).</summary>
internal static class SignInServices
{
    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddIdentityAccounts();
        services.AddDeviceStores();

        services.AddSingleton<SignInIntentCookie>();
        services.AddScoped<SignInFlow>();
        services.AddScoped<SpaPage>();
        services.AddScoped<IssueCode.Handler>();
        services.AddScoped<VerifyCode.Handler>();
        services.AddScoped<PlanSignIn.Handler>();
        services.AddScoped<CompleteSignIn.Handler>();
        services.AddScoped<GetSetupStatus.Handler>();
        services.AddScoped<ListSignInMethods.Handler>();
        services.AddScoped<RemoveSignInMethod.Handler>();
        services.AddHostedService<SetupCodeBootstrapper>();

        // A setup code or an invite token may be in a URL or a body, and the Auth0 callback carries a code; the framework's request log
        // would print the URL. The router's debug lines print the path too.
        services.AddLogging(logging => logging
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Routing.Matching", LogLevel.Information));
        return services;
    }
}
