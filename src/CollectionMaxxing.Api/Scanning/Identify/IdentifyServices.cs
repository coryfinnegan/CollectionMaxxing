using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Infrastructure.Jobs;

namespace CollectionMaxxing.Api.Scanning.Identify;

/// <summary>Registers <c>/identify</c> (SCAN-2a). Called from <see cref="ScanningServices.AddScanning"/>.</summary>
public static class IdentifyServices
{
    public static IServiceCollection AddIdentify(this IServiceCollection services)
    {
        // IOptionsMonitor<ScoringOptions> needs no registration of its own: until SCAN-2d binds the live options it yields the doc's defaults.
        services.AddScoped<IdentifyGrounding>();
        services.AddScoped<IdentifyItems.Handler>();
        services.AddJobHandler<IdentifyItems.ResolveJob, ResolvePendingIdentify>(IdentifyItems.ResolveJobType);
        return services;
    }
}
