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

        // Not ValidateOnStart: the reader is optional, and a bad setting makes a read "unavailable" with a log line instead of stopping the API.
        services.AddOptions<ShelfReaderOptions>().BindConfiguration(ShelfReaderOptions.SectionName)
            .Validate(ShelfReaderOptions.IsValid, "ShelfReader: Endpoint must be empty or an http or https address; MaxTokens, MaxLongEdge, TimeoutSeconds and MaxRepeats must be above 0.");

        // HttpClient.Timeout (100 s by default) would cut a 120 s read; the reader enforces TimeoutSeconds itself with a linked token.
        var client = services.AddHttpClient<IShelfReader, LlamaServerShelfReader>(http => http.Timeout = Timeout.InfiniteTimeSpan);

        // The app gives every HttpClient the standard resilience handler (ServiceDefaults): 10 s attempts, a 30 s total, retries and
        // a breaker. A shelf read takes 10 to 25 s and a retry would double a GPU call, so the reader has none of it.
#pragma warning disable EXTEXP0001 // Experimental only in name; it is the supported way to drop the app-wide default.
        client.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.AddSingleton<IShelfStillMaker, ShelfStillMaker>();
        return services;
    }
}
