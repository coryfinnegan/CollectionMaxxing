using CollectionMaxxing.Application.Recognition;
using CollectionMaxxing.Infrastructure.Recognition;

namespace CollectionMaxxing.Api.Recognition;

/// <summary>Registers the recognition config feature. Kept apart from the endpoints class, which may not know Infrastructure.</summary>
public static class RecognitionServices
{
    /// <summary>
    /// Registers the live recognition settings (the checked-in defaults, merged with the optional override file named by
    /// <c>Recognition:ConfigPath</c>), <c>IOptionsMonitor&lt;ScoringOptions&gt;</c> for the identify endpoints, and the handler.
    /// Called from <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddRecognition(this IServiceCollection services)
    {
        services.AddOptions<RecognitionOptions>().BindConfiguration(RecognitionOptions.SectionName);
        services.AddRecognitionSettings();
        services.AddSingleton<GetRecognitionConfig.Handler>();
        return services;
    }
}
