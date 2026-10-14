using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Infrastructure.Ids;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Jobs;

public sealed record Ping(string Label);

/// <summary>Records what the test handlers did, shared by every handler instance.</summary>
public sealed class JobLog
{
    public List<string> Ran { get; } = [];

    public Func<Ping, CancellationToken, Task>? Behaviour { get; set; }

    public void Add(string label)
    {
        lock (Ran)
        {
            Ran.Add(label);
        }
    }
}

public sealed class PingHandler(JobLog log) : IJobHandler<Ping>
{
    public async Task HandleAsync(Ping payload, CancellationToken cancellationToken)
    {
        log.Add(payload.Label);
        if (log.Behaviour is { } behaviour)
        {
            await behaviour(payload, cancellationToken);
        }
    }
}

public sealed class TickPayload;

public sealed class TickHandler(JobLog log) : IJobHandler<TickPayload>
{
    public Task HandleAsync(TickPayload payload, CancellationToken cancellationToken)
    {
        log.Add("tick");
        return Task.CompletedTask;
    }
}

/// <summary>One "process": its own service provider over the shared test database. Create two to simulate two workers.</summary>
public sealed class JobHarness : IAsyncDisposable
{
    public const string PingType = "test.ping";
    public const string TickType = "test.tick";

    private readonly ServiceProvider _services;

    public JobHarness(PostgresFixture db, FakeTimeProvider? time = null, Action<JobOptions>? tune = null, TimeSpan? tickEvery = null)
    {
        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IIdGenerator, Uuid7IdGenerator>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(AppDbContextOptions.ConfigureIdentityStores);
        services.AddDbContext<AppDbContext>(o => AppDbContextOptions.Configure(o, db.ConnectionString));
        services.AddSingleton(Log);
        services.AddJobs();
        services.AddJobHandler<Ping, PingHandler>(PingType);
        services.AddJobHandler<TickPayload, TickHandler>(TickType);
        if (tickEvery is { } every)
        {
            services.AddRecurringJob(TickType, every);
        }

        services.Configure<JobOptions>(options =>
        {
            options.PollInterval = TimeSpan.FromSeconds(1);
            tune?.Invoke(options);
        });
        _services = services.BuildServiceProvider();
    }

    public FakeTimeProvider Time { get; }

    public JobLog Log { get; } = new();

    public JobRunner Runner => _services.GetRequiredService<JobRunner>();

    public RecurringJobScheduler Scheduler => _services.GetRequiredService<RecurringJobScheduler>();

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>Queues a job the way a feature would.</summary>
    public async Task<Guid?> EnqueueAsync(string type, object? payload, JobEnqueueOptions? options = null)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(type, payload, options);
    }

    public static async Task<List<Job>> JobsAsync(PostgresFixture db)
    {
        await using var context = db.CreateContext();
        return await context.Jobs.AsNoTracking().OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).ToListAsync();
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();
}
