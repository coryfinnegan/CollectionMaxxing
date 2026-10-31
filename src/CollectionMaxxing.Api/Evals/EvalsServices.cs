using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.Infrastructure.GameSerials;

namespace CollectionMaxxing.Api.Evals;

/// <summary>Registers the eval server. Kept apart from the endpoints class, which may not know Infrastructure.</summary>
public static class EvalsServices
{
    /// <summary>
    /// Binds the <c>Evals</c> settings (off unless <c>Evals:Enabled</c> is true), the file-based golden set and results store, the
    /// dry-run grounder and the handlers. Called from <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddEvals(this IServiceCollection services)
    {
        services.AddOptions<EvalsOptions>().BindConfiguration(EvalsOptions.SectionName);
        services.AddSingleton<IGoldenSet, FileGoldenSet>();
        services.AddSingleton<IEvalResults, FileEvalResults>();
        services.AddGameSerials(); // the grounder reads product codes the way /identify does (ADR-031)
        services.AddScoped<EvalGrounder>();
        services.AddScoped<ListGoldenPhotos.Handler>();
        services.AddScoped<GetGoldenImage.Handler>();
        services.AddScoped<AddGoldenPhoto.Handler>();
        services.AddScoped<GetLatestEvalRun.Handler>();
        services.AddScoped<SubmitEvalRun.Handler>();

        // The resumable run: kept as files on the PC, grounded in the background as the phone sends each photo, scored once when it finishes.
        services.AddSingleton<IEvalRunStore, FileEvalRunStore>();
        services.AddSingleton<EvalRunCoordinator>();
        services.AddScoped<EvalRunScoring>();
        services.AddScoped<EvalRunProcessor>();
        services.AddScoped<OpenEvalRun.Handler>();
        services.AddScoped<GetOpenEvalRun.Handler>();
        services.AddScoped<StoreEvalPhoto.Handler>();
        services.AddScoped<FinishEvalRun.Handler>();
        services.AddScoped<DiscardEvalRun.Handler>();
        services.AddHostedService<EvalRunWorker>();
        return services;
    }
}
