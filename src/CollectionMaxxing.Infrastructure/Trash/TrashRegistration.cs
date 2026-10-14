using CollectionMaxxing.Application.Trash;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.Trash;

public static class TrashRegistration
{
    /// <summary>Registers the daily job that purges items deleted more than 30 days ago. Needs <c>AddJobs()</c> and <c>AddAudit()</c>.</summary>
    public static IServiceCollection AddTrashPurge(this IServiceCollection services)
    {
        services.AddJobHandler<PurgeDeletedItemsPayload, PurgeDeletedItems.Handler>(PurgeDeletedItems.JobType);
        services.AddRecurringJob(PurgeDeletedItems.JobType, PurgeDeletedItems.Every);
        return services;
    }
}
