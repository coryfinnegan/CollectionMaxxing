using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Infrastructure.Exports;
using CollectionMaxxing.Infrastructure.Tests.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Tests.Exports;

public class ExportJobsTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public async Task Finding_an_export_with_duplicate_rows_reports_the_latest_job()
    {
        await using var harness = new JobHarness(Db);
        var userId = Guid.CreateVersion7();
        var exportId = Guid.CreateVersion7();
        var stale = await harness.EnqueueAsync(BuildExport.JobType, new BuildExport.Payload(exportId, userId));
        var latest = await harness.EnqueueAsync(BuildExport.JobType, new BuildExport.Payload(exportId, userId));
        var now = harness.Time.GetUtcNow();
        await using (var context = Db.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE jobs SET status = 'failed', created_at = {now - TimeSpan.FromHours(1)} WHERE id = {stale}");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE jobs SET status = 'succeeded', created_at = {now} WHERE id = {latest}");
        }

        await using var lookup = Db.CreateContext();
        var found = (await new ExportJobs(lookup).FindAsync(exportId, CancellationToken.None)).ShouldNotBeNull();

        found.UserId.ShouldBe(userId);
        found.JobStatus.ShouldBe("succeeded");
    }
}
