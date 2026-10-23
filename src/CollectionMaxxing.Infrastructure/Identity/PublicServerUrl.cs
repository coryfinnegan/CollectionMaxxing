using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Accounts.IdentityProvider;
using CollectionMaxxing.Infrastructure.Identity.Auth0;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>Settings under <c>Server:</c>.</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>The older name for <c>Auth:PublicOrigin</c>, used only when that isn't set.</summary>
    public string? PublicUrl { get; set; }
}

/// <summary>
/// The server's own address, for invite links: <c>Auth:PublicOrigin</c>, else <c>Server:PublicUrl</c>. Never learned from a request, because
/// a forged Host header must not be able to point a link at somebody else's server.
/// </summary>
internal sealed class ConfiguredServerUrl(IOptions<AuthOptions> auth, IOptions<ServerOptions> server) : IPublicServerUrl
{
    public string Value =>
        auth.Value.PublicOriginOrNull
        ?? (string.IsNullOrWhiteSpace(server.Value.PublicUrl) ? "https://localhost" : server.Value.PublicUrl.Trim().TrimEnd('/'));
}

public static class DeviceRegistration
{
    /// <summary>
    /// The configured public address, the iPhones that signed in with Auth0, and the Management API behind signing them out, removing a
    /// member and deleting an account. Needs <c>AddInfrastructure()</c> and <c>AddJobs()</c>.
    /// </summary>
    public static IServiceCollection AddDeviceStores(this IServiceCollection services)
    {
        services.AddOptions<ServerOptions>().BindConfiguration(ServerOptions.SectionName);
        services.TryAddScoped<IDeviceAuthorizations, AppSignInDevices>();
        services.TryAddScoped<ITokenCutoffs, IdentityTokenCutoffs>();
        services.AddHttpClient(Auth0Management.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
        services.TryAddSingleton<Auth0IdentityProviderAdmin>();
        services.TryAddSingleton<IIdentityProviderAdmin>(provider =>
            string.IsNullOrWhiteSpace(provider.GetRequiredService<IOptions<AuthOptions>>().Value.Auth0.ManagementClientId)
                ? ActivatorUtilities.CreateInstance<UnconfiguredIdentityProviderAdmin>(provider)
                : provider.GetRequiredService<Auth0IdentityProviderAdmin>());
        services.AddJobHandler<RevokeIdentitySessionsPayload, RevokeIdentitySessions.Handler>(RevokeIdentitySessions.JobType);
        services.AddScoped<IUserPurgeStep, DeleteIdentityProviderUsers>();
        services.TryAddSingleton<IPublicServerUrl, ConfiguredServerUrl>();
        return services;
    }
}

/// <summary>The name of the <see cref="HttpClient"/> that talks to Auth0's Management API (tests point it at a fake tenant).</summary>
public static class Auth0Management
{
    public const string HttpClientName = "auth0-management";
}

/// <summary><c>users.tokens_not_before</c> behind <see cref="ITokenCutoffs"/>.</summary>
internal sealed class IdentityTokenCutoffs(AppDbContext db) : ITokenCutoffs
{
    public async Task RaiseAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await db.Users
            .Where(u => u.Id == userId && (u.TokensNotBefore == null || u.TokensNotBefore < at))
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.TokensNotBefore, at), cancellationToken);

    public Task<DateTimeOffset?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TokensNotBefore).SingleOrDefaultAsync(cancellationToken);
}
