using CollectionMaxxing.Infrastructure.Providers;

namespace CollectionMaxxing.Api.Catalog;

public static class ProviderRegistration
{
    /// <summary>
    /// The outside catalogue providers (Open Library, Google Books, UPCitemdb, TMDB, IGDB), configured under
    /// <c>Providers:*</c>. Keys come from configuration only; a provider without its key is switched off.
    /// </summary>
    public static IServiceCollection AddCatalogProviders(this IServiceCollection services) =>
        services.AddCatalogProviderAdapters();
}
