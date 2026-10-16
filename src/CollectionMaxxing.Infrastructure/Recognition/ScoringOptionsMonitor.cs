using CollectionMaxxing.Application.Recognition;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Recognition;

/// <summary>
/// The scoring options as an <see cref="IOptionsMonitor{TOptions}"/>, so the identify endpoints read the current weights and
/// thresholds on every use. <c>CurrentValue</c> is always the latest valid override (or the documented defaults).
/// </summary>
/// <remarks>Change callbacks are not raised: read <c>CurrentValue</c> each time instead of caching it.</remarks>
public sealed class ScoringOptionsMonitor(IRecognitionSettings settings) : IOptionsMonitor<ScoringOptions>
{
    public ScoringOptions CurrentValue => settings.Scoring;

    public ScoringOptions Get(string? name) => settings.Scoring;

    public IDisposable? OnChange(Action<ScoringOptions, string?> listener) => null;
}

public static class RecognitionInfrastructure
{
    /// <summary>
    /// Registers the live recognition settings and <c>IOptionsMonitor&lt;ScoringOptions&gt;</c>. The caller binds
    /// <see cref="RecognitionOptions"/> from configuration (the <c>Recognition</c> section).
    /// </summary>
    public static IServiceCollection AddRecognitionSettings(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RecognitionConfigStore>();
        services.TryAddSingleton<IRecognitionSettings>(provider => provider.GetRequiredService<RecognitionConfigStore>());
        services.Replace(ServiceDescriptor.Singleton<IOptionsMonitor<ScoringOptions>, ScoringOptionsMonitor>());
        return services;
    }
}
