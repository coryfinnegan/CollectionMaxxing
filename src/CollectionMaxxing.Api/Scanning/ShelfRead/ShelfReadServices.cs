using CollectionMaxxing.Application.Scanning.ShelfReading;
using CollectionMaxxing.Application.ShelfReading;

namespace CollectionMaxxing.Api.Scanning.ShelfRead;

/// <summary>Registers the server-side shelf read (ADR-033). Called from <see cref="ScanningServices.AddScanning"/>; the reader itself is registered by <c>AddInfrastructure()</c>.</summary>
public static class ShelfReadServices
{
    public static IServiceCollection AddShelfRead(this IServiceCollection services)
    {
        // The queue in front of the one card: ShelfReader:QueueWaitSeconds and ShelfReader:QueueMaxWaiting, next to the reader's own settings.
        services.AddOptions<ShelfReadQueueOptions>().BindConfiguration("ShelfReader");
        services.AddSingleton<ShelfReadGate>();
        services.AddScoped<ReadShelfSnap.Handler>();
        return services;
    }
}
