using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Application.Scanning.Quota;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Scanning.Events;

/// <summary>
/// What happens around a scan session (SCAN-1b): the event stream, the UPC quota queue and its nightly drain, the learning
/// loop, and the daily clean-up of abandoned sessions. Called from <c>AddScanning()</c>.
/// </summary>
public static class ScanFollowUpServices
{
    public static IServiceCollection AddScanFollowUps(this IServiceCollection services)
    {
        services.TryAddSingleton<ScanEventOptions>();
        services.AddScoped<ScanEventFeed>();

        services.AddScoped<BarcodeLearner>();
        services.AddScoped<MatchFeedbackRecorder>();

        services.AddJobHandler<DrainQuotaBacklog.Job, DrainQuotaBacklog>(DrainQuotaBacklog.JobType);
        services.AddRecurringJob(DrainQuotaBacklog.JobType, TimeSpan.FromDays(1));

        services.AddJobHandler<CleanUpScanSessions.Job, CleanUpScanSessions>(CleanUpScanSessions.JobType);
        services.AddRecurringJob(CleanUpScanSessions.JobType, TimeSpan.FromDays(1));
        return services;
    }
}
