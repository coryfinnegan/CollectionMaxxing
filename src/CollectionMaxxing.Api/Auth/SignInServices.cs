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
        services.AddScoped<SetDevTools.Handler>();
        services.AddScoped<VerifyCode.Handler>();
        services.AddScoped<PlanSignIn.Handler>();
        services.AddScoped<CompleteSignIn.Handler>();
        services.AddScoped<CreateAccount.Handler>();
        services.AddScoped<ListSignInMethods.Handler>();
        services.AddScoped<RemoveSignInMethod.Handler>();

        // A first bearer call asks Auth0 who the caller is, once (docs/15).
        services.AddSingleton<Auth0UserInfo>();
        services.AddHttpClient(Auth0UserInfo.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));

        // A recovery code or an invite token may be in a URL or a body, and the Auth0 callback carries a code; the framework's request log
        // would print the URL. The router's debug lines print the path too.
        services.AddLogging(logging => logging
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Routing.Matching", LogLevel.Information)
            .AddFilter("System.Net.Http.HttpClient." + Auth0UserInfo.HttpClientName, LogLevel.Warning));
        return services;
    }
}
