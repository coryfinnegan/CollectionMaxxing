using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.Audit;

public static class AuditRegistration
{
    /// <summary>
    /// Registers the audit writer, the anonymiser and the daily one-year retention job. Needs <c>AddInfrastructure()</c> and
    /// <c>AddJobs()</c>. A web host registers its own <see cref="IRequestOrigin"/> first so events carry the caller's address hash.
    /// </summary>
    public static IServiceCollection AddAudit(this IServiceCollection services)
    {
        services.AddOptions<AuditOptions>().BindConfiguration("Audit");
        services.TryAddScoped<IRequestOrigin, NoRequestOrigin>();
        services.TryAddScoped<IAuditLog, AuditLog>();
        services.TryAddScoped<AnonymiseAuditActor.Handler>();
        services.AddJobHandler<PruneAuditEventsPayload, PruneAuditEvents.Handler>(PruneAuditEvents.JobType);
        services.AddRecurringJob(PruneAuditEvents.JobType, PruneAuditEvents.Every);
        return services;
    }
}
