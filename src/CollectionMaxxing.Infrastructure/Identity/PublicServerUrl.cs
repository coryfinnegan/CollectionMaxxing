using CollectionMaxxing.Application.Accounts.Devices;
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

/// <summary>Phones sign in with Auth0 and are not registered with the server until AUTH-2 (#229), so nobody has any yet.</summary>
internal sealed class NoDeviceAuthorizations : IDeviceAuthorizations
{
    public Task<IReadOnlyList<DeviceSession>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceSession>>([]);

    public Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class DeviceRegistration
{
    /// <summary>The configured public address and the (still empty) list of phones.</summary>
    public static IServiceCollection AddDeviceStores(this IServiceCollection services)
    {
        services.AddOptions<ServerOptions>().BindConfiguration(ServerOptions.SectionName);
        services.TryAddScoped<IDeviceAuthorizations, NoDeviceAuthorizations>();
        services.TryAddSingleton<IPublicServerUrl, ConfiguredServerUrl>();
        return services;
    }
}
