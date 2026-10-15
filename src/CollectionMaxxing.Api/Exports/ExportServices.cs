using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Infrastructure.Exports;

namespace CollectionMaxxing.Api.Exports;

public static class ExportServices
{
    /// <summary>Registers this feature's handlers, file store and jobs.</summary>
    public static IServiceCollection AddExports(this IServiceCollection services)
    {
        services.AddExportStorage();
        services.AddSingleton<ExportLinks>();
        services.AddScoped<StartExport.Handler>();
        services.AddScoped<GetExport.Handler>();
        services.AddScoped<OpenExport.Handler>();
        return services;
    }
}
