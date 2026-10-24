using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Infrastructure.Providers.Caching;
using CollectionMaxxing.Infrastructure.Providers.GoogleBooks;
using CollectionMaxxing.Infrastructure.Providers.Igdb;
using CollectionMaxxing.Infrastructure.Providers.OpenLibrary;
using CollectionMaxxing.Infrastructure.Providers.Quota;
using CollectionMaxxing.Infrastructure.Providers.Resilience;
using CollectionMaxxing.Infrastructure.Providers.Tmdb;
using CollectionMaxxing.Infrastructure.Providers.UpcItemDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Providers;

public static class ProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the five provider adapters as <see cref="ICatalogProvider"/>s. Settings come from <c>Providers:*</c>
    /// configuration (user secrets or environment variables for keys). A provider without its key stays registered but
    /// reports <c>Supports(...) == false</c>, so one missing key never stops the others or the app.
    /// </summary>
    public static IServiceCollection AddCatalogProviderAdapters(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ProviderRateLimiters>();
        services.AddSingleton<ProviderResponseCache>();
        services.AddSingleton<ProviderQuotaStore>();
        services.AddSingleton<ProviderRuntime>();
        services.TryAddSingleton<IIsbnStore, ProviderCacheIsbnStore>();

        services.AddOptions<ProviderResilienceOptions>().BindConfiguration(ProviderResilienceOptions.SectionName);
        services.AddOptions<OpenLibraryOptions>().BindConfiguration(OpenLibraryOptions.SectionName);
        services.AddOptions<GoogleBooksOptions>().BindConfiguration(GoogleBooksOptions.SectionName);
        services.AddOptions<UpcItemDbOptions>().BindConfiguration(UpcItemDbOptions.SectionName);
        services.AddOptions<TmdbOptions>().BindConfiguration(TmdbOptions.SectionName);
        services.AddOptions<IgdbOptions>().BindConfiguration(IgdbOptions.SectionName);

        services.AddHttpClient<OpenLibraryProvider>(OpenLibraryProvider.ProviderName).AddProviderResilience();
        services.AddHttpClient<GoogleBooksProvider>(GoogleBooksProvider.ProviderName).AddProviderResilience();
        services.AddHttpClient<UpcItemDbProvider>(UpcItemDbProvider.ProviderName).AddProviderResilience();
        services.AddHttpClient<TmdbProvider>(TmdbProvider.ProviderName).AddProviderResilience();
        services.AddHttpClient<IgdbProvider>(IgdbProvider.ProviderName).AddProviderResilience();

        // The token source is a singleton so the token is shared; its client is created once from the factory.
        services.AddHttpClient(IgdbProvider.ProviderName + "-token").AddProviderResilience();
        services.AddSingleton(sp => new IgdbTokenSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(IgdbProvider.ProviderName + "-token"),
            sp.GetRequiredService<IOptions<IgdbOptions>>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddTransient<ICatalogProvider>(sp => sp.GetRequiredService<OpenLibraryProvider>());
        services.AddTransient<ICatalogProvider>(sp => sp.GetRequiredService<GoogleBooksProvider>());
        services.AddTransient<ICatalogProvider>(sp => sp.GetRequiredService<UpcItemDbProvider>());
        services.AddTransient<ICatalogProvider>(sp => sp.GetRequiredService<TmdbProvider>());
        services.AddTransient<ICatalogProvider>(sp => sp.GetRequiredService<IgdbProvider>());
        return services;
    }
}
