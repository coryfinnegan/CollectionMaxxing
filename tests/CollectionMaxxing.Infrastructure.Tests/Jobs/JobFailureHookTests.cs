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

public sealed record Shaky(string Label);

/// <summary>What the shaky handlers saw, shared across scopes.</summary>
public sealed class ShakyLog
{
    public List<string> Ran { get; } = [];

    public List<(string Label, string Error)> GaveUp { get; } = [];

    public bool Fail { get; set; } = true;

    public bool HookThrows { get; set; }
}

/// <summary>A handler that can fail, and wants to hear when its job has failed for good.</summary>
public sealed class ShakyHandler(ShakyLog log) : IJobHandler<Shaky>, IJobFailureHandler<Shaky>
{
    public Task HandleAsync(Shaky payload, CancellationToken cancellationToken)
    {
        log.Ran.Add(payload.Label);
        return log.Fail ? throw new InvalidOperationException("upstream said no (password=hunter2)") : Task.CompletedTask;
    }

    public Task OnFailedForGoodAsync(Shaky payload, string lastError, CancellationToken cancellationToken)
    {
        log.GaveUp.Add((payload.Label, lastError));
        return log.HookThrows ? throw new InvalidOperationException("the hook broke too") : Task.CompletedTask;
    }
}

/// <summary>Registered without the hook: the runner must not need one.</summary>
public sealed class PlainFailingHandler : IJobHandler<Shaky>
{
    public Task HandleAsync(Shaky payload, CancellationToken cancellationToken) => throw new InvalidOperationException("plain failure");
}

public class JobFailureHookTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string ShakyType = "test.shaky";
    private const string PlainType = "test.plain";

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public Rig(PostgresFixture db, int maxAttempts)
        {
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IIdGenerator, Uuid7IdGenerator>();
            services.AddScoped<ICurrentUser, CurrentUser>();
            services.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(AppDbContextOptions.ConfigureIdentityStores);
            services.AddDbContext<AppDbContext>(o => AppDbContextOptions.Configure(o, db.ConnectionString));
            services.AddSingleton(Log);
            services.AddJobs();
            services.AddJobHandler<Shaky, ShakyHandler>(ShakyType);
            services.AddJobHandler<Shaky, PlainFailingHandler>(PlainType);
            services.Configure<JobOptions>(o => o.Retry = new JobRetryPolicy { MaxAttempts = maxAttempts, BaseDelay = TimeSpan.FromSeconds(10) });
            _services = services.BuildServiceProvider();
        }

        public FakeTimeProvider Time { get; }

        public ShakyLog Log { get; } = new();

        public JobRunner Runner => _services.GetRequiredService<JobRunner>();

        public async Task EnqueueAsync(string type, string label)
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(type, new Shaky(label));
        }

        public async ValueTask DisposeAsync() => await _services.DisposeAsync();
    }

    private async Task<List<Job>> JobsAsync()
    {
        await using var context = Db.CreateContext();
        return await context.Jobs.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task A_job_that_fails_for_the_last_time_tells_its_handler_once_with_its_payload_and_the_error()
    {
        await using var rig = new Rig(Db, maxAttempts: 2);
        await rig.EnqueueAsync(ShakyType, "last");

        (await rig.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeTrue();
        rig.Log.GaveUp.ShouldBeEmpty("a retry is still coming");
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        (await rig.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeTrue();

        (await JobsAsync()).Single().Status.ShouldBe(JobStatus.Failed);
        var told = rig.Log.GaveUp.ShouldHaveSingleItem();
        told.Label.ShouldBe("last");
        told.Error.ShouldContain("upstream said no");
        told.Error.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task A_job_that_succeeds_never_tells_its_failure_hook()
    {
        await using var rig = new Rig(Db, maxAttempts: 1);
        rig.Log.Fail = false;
        await rig.EnqueueAsync(ShakyType, "fine");

        await rig.Runner.RunNextAsync("w", CancellationToken.None);

        (await JobsAsync()).Single().Status.ShouldBe(JobStatus.Succeeded);
        rig.Log.GaveUp.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_abandoned_on_its_last_attempt_tells_its_failure_hook_without_running_again()
    {
        await using var rig = new Rig(Db, maxAttempts: 2);
        await rig.EnqueueAsync(ShakyType, "poison");
        await using (var context = Db.CreateContext())
        {
            var past = rig.Time.GetUtcNow() - TimeSpan.FromMinutes(1);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE jobs SET status = 'running', attempts = 2, locked_by = 'dead', locked_until = {past}");
        }

        await rig.Runner.RunNextAsync("w", CancellationToken.None);

        rig.Log.Ran.ShouldBeEmpty();
        (await JobsAsync()).Single().Status.ShouldBe(JobStatus.Failed);
        rig.Log.GaveUp.ShouldHaveSingleItem().Label.ShouldBe("poison");
    }

    [Fact]
    public async Task A_failure_hook_that_throws_does_not_stop_the_runner_or_unfail_the_job()
    {
        await using var rig = new Rig(Db, maxAttempts: 1);
        rig.Log.HookThrows = true;
        await rig.EnqueueAsync(ShakyType, "double fault");

        var picked = await rig.Runner.RunNextAsync("w", CancellationToken.None);

        picked.ShouldBeTrue();
        (await JobsAsync()).Single().Status.ShouldBe(JobStatus.Failed);
        rig.Log.GaveUp.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_handler_without_a_failure_hook_just_fails_as_before()
    {
        await using var rig = new Rig(Db, maxAttempts: 1);
        await rig.EnqueueAsync(PlainType, "plain");

        (await rig.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeTrue();

        (await JobsAsync()).Single().Status.ShouldBe(JobStatus.Failed);
    }
}
