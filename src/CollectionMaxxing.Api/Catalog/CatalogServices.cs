using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.FeaturePrints;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Infrastructure.Catalog;

namespace CollectionMaxxing.Api.Catalog;

/// <summary>Registers the catalogue feature. Kept apart from the endpoints class, which may not know Infrastructure.</summary>
public static class CatalogServices
{
    /// <summary>Registers this feature's handlers and storage. Called from <c>Program.cs</c>.</summary>
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.AddSingleton<CatalogOptions>();
        services.AddCatalogStorage();
        services.AddCatalogProviders();
        services.AddScoped<SearchCatalog.Handler>();
        services.AddScoped<LookupCatalog.Handler>();
        services.AddScoped<BookLookup>(); // ISBN lookups: the permanent local copy, then the book providers
        services.AddScoped<GetRelease.Handler>();
        services.AddScoped<ListReleaseArtwork.Handler>();
        services.AddScoped<ListPlatforms.Handler>();
        services.AddScoped<GetCover.Handler>();
        services.AddScoped<ListFeaturePrints.Handler>(); // SCAN-2c
        services.AddScoped<PutFeaturePrints.Handler>();
        return services;
    }
}
