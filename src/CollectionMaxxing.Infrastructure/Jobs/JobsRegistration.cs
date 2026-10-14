using System.Text.Json;
using CollectionMaxxing.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.Jobs;

/// <summary>Tuning for the job worker. The defaults suit a single home server.</summary>
public sealed class JobOptions
{
    public JobRetryPolicy Retry { get; set; } = new();

    /// <summary>How long a worker owns a job it picked up. After this a job is treated as abandoned (the worker died) and runs again.</summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the worker sleeps when there is nothing to do.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>A handler registered under a type name. <see cref="Invoke"/> deserializes the payload and calls the handler.</summary>
public sealed record JobRegistration(string Type, Func<IServiceProvider, JsonElement, CancellationToken, Task> Invoke);

/// <summary>A job that should exist once per <see cref="Every"/> period, across all workers.</summary>
public sealed record RecurringJobRegistration(string Type, TimeSpan Every);

public static class JobsRegistration
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Registers the queue, the runner and the hosted worker. Called once from <c>Program.cs</c>; needs <c>AddInfrastructure()</c>.</summary>
    public static IServiceCollection AddJobs(this IServiceCollection services)
    {
        services.AddOptions<JobOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<JobRunner>();
        services.TryAddSingleton<RecurringJobScheduler>();
        services.TryAddScoped<IJobQueue, JobQueue>();
        services.AddHostedService<JobWorker>();
        return services;
    }

    /// <summary>Registers the handler that runs jobs of <paramref name="type"/>. Each feature calls this for its own jobs.</summary>
    public static IServiceCollection AddJobHandler<TPayload, THandler>(this IServiceCollection services, string type)
        where THandler : class, IJobHandler<TPayload>
    {
        services.TryAddScoped<THandler>();
        services.AddSingleton(new JobRegistration(type, (provider, payload, cancellationToken) =>
        {
            var value = payload.Deserialize<TPayload>(Json)
                ?? throw new InvalidOperationException($"Job payload for '{type}' is empty.");
            return provider.GetRequiredService<THandler>().HandleAsync(value, cancellationToken);
        }));
        return services;
    }

    /// <summary>Makes sure a job of <paramref name="type"/> (payload <c>{}</c>) is queued once every <paramref name="every"/>, however many workers run.</summary>
    public static IServiceCollection AddRecurringJob(this IServiceCollection services, string type, TimeSpan every)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(every, TimeSpan.Zero);
        services.AddSingleton(new RecurringJobRegistration(type, every));
        return services;
    }
}
