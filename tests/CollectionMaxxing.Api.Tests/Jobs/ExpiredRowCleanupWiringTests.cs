using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Jobs;

/// <summary>The running app queues the expired-row cleanup once a day, and the handler it queues works against the real container.</summary>
public class ExpiredRowCleanupWiringTests(ApiFixture api) : ApiTest(api)
{
    [Fact]
    public void The_app_queues_the_cleanup_once_a_day()
    {
        var recurring = Api.Factory.Services.GetServices<RecurringJobRegistration>().ToList();

        recurring.ShouldContain(r => r.Type == CleanExpiredRows.JobType && r.Every == TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task The_queued_job_removes_an_expired_key_and_keeps_a_live_one()
    {
        var user = await TestData.CreateUserAsync(Db);
        await using (var context = Db.CreateContext())
        {
            foreach (var (key, expiresAt) in new[] { ("expired", new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)), ("live", new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero)) })
            {
                context.IdempotencyKeys.Add(new IdempotencyKey
                {
                    UserId = user,
                    Key = key,
                    RequestHash = "hash",
                    ResponseStatus = 201,
                    CreatedAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    ExpiresAt = expiresAt,
                });
            }

            await context.SaveChangesAsync();
        }

        var registration = Api.Factory.Services.GetServices<JobRegistration>().Single(r => r.Type == CleanExpiredRows.JobType);
        using var scope = Api.Factory.Services.CreateScope();
        await registration.Invoke(scope.ServiceProvider, JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        await using var check = Db.CreateContext();
        (await check.IdempotencyKeys.AsNoTracking().Select(k => k.Key).ToListAsync()).ShouldBe(["live"]);
    }
}
