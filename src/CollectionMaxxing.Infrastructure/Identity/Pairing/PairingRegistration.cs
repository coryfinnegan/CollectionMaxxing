using CollectionMaxxing.Application.Accounts.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Identity.Pairing;

/// <summary>Settings under <c>Server:</c>.</summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>
    /// The address the phone is told to use, for example <c>https://pc.tailnet.ts.net</c>. Never learned from a request, because a
    /// forged Host header must not be able to point a phone at somebody else's server. Falls back to https on the relying-party host.
    /// </summary>
    public string? PublicUrl { get; set; }
}

internal sealed class ConfiguredServerUrl(IOptions<ServerOptions> server, IOptions<AuthOptions> auth) : IPublicServerUrl
{
    public string Value
    {
        get
        {
            var configured = server.Value.PublicUrl;
            return string.IsNullOrWhiteSpace(configured)
                ? $"https://{auth.Value.RelyingPartyId}"
                : configured.Trim().TrimEnd('/');
        }
    }
}

public static class PairingRegistration
{
    /// <summary>The OpenIddict-backed device store and the configured public address.</summary>
    public static IServiceCollection AddDevicePairingStores(this IServiceCollection services)
    {
        services.AddOptions<ServerOptions>().BindConfiguration(ServerOptions.SectionName);
        services.TryAddScoped<IDeviceAuthorizations, OpenIddictDeviceAuthorizations>();

        // Housekeeping: spent and expired pairing rows go once they are a day old.
        services.AddOptions<PairingCleanupOptions>().BindConfiguration(PairingCleanupOptions.SectionName);
        services.TryAddScoped<IDevicePairingCleanup, DevicePairingCleanup>();
        services.AddHostedService<PairingCleanupService>();
        services.TryAddSingleton<IPublicServerUrl, ConfiguredServerUrl>();
        return services;
    }
}
