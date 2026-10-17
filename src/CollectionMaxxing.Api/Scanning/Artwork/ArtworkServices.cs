using CollectionMaxxing.Application.Scanning.Artwork;

namespace CollectionMaxxing.Api.Scanning.Artwork;

/// <summary>Registers <c>:art</c> (SCAN-2b). Called from <see cref="ScanningServices.AddScanning"/>.</summary>
public static class ArtworkServices
{
    public static IServiceCollection AddArtwork(this IServiceCollection services)
    {
        services.AddScoped<ScoreCandidateArtwork.Handler>();
        return services;
    }
}
