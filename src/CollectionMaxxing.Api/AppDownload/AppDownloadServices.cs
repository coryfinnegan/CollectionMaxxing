using CollectionMaxxing.Application.AppDownload;
using CollectionMaxxing.Infrastructure.AppDownload;

namespace CollectionMaxxing.Api.AppDownload;

public static class AppDownloadServices
{
    /// <summary>Registers this feature's handlers and the file store.</summary>
    public static IServiceCollection AddAppDownload(this IServiceCollection services)
    {
        services.AddIpaStorage();
        services.AddScoped<GetIpaInfo.Handler>();
        services.AddScoped<OpenIpa.Handler>();
        return services;
    }
}
