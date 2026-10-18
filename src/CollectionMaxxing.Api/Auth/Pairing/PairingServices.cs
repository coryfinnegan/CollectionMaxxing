using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Infrastructure.Identity.Pairing;

namespace CollectionMaxxing.Api.Auth.Pairing;

/// <summary>The services behind device pairing, refresh rotation and the devices list.</summary>
public static class PairingServices
{
    public static IServiceCollection AddDevicePairing(this IServiceCollection services)
    {
        services.AddDevicePairingStores();
        services.AddScoped<CreatePairing.Handler>();
        services.AddScoped<RedeemPairing.Handler>();
        services.AddScoped<ListSessions.Handler>();
        services.AddScoped<RevokeSession.Handler>();
        services.AddScoped<ListWebSessions.Handler>();
        services.AddScoped<RevokeWebSession.Handler>();
        services.AddScoped<RevokeWebSessions.Handler>();
        services.AddSingleton<PairingAttemptLimiter>();
        return services;
    }
}
