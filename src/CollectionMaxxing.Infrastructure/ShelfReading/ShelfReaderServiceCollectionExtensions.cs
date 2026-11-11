using CollectionMaxxing.Application.ShelfReading;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.ShelfReading;

public static class ShelfReaderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shelf reader (ADR-033) and the still maker. Safe to call more than once. The reader has its own named
    /// client with no resilience pipeline: one timeout from <c>ShelfReader:TimeoutSeconds</c>, no retry, no breaker. With no
    /// <c>ShelfReader:Endpoint</c> it is off and calls nothing.
    /// </summary>
    public static IServiceCollection AddShelfReader(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IShelfReader)))
        {
            return services;
        }

        services.AddOptions<ShelfReaderOptions>().BindConfiguration(ShelfReaderOptions.SectionName);
        services.AddHttpClient<IShelfReader, LlamaServerShelfReader>(client => client.Timeout = Timeout.InfiniteTimeSpan); // the reader enforces its own timeout
        services.AddSingleton<IShelfStillMaker, ShelfStillMaker>();
        return services;
    }
}
