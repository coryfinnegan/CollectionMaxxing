using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Admin;

/// <summary>Who may call GET /api/admin/jobs is covered by the authorization matrix; this is about what it returns.</summary>
public class AdminJobsTests(ApiFixture api) : ApiTest(api)
{
    [Fact]
    public async Task The_admin_sees_failed_and_pending_jobs_but_not_finished_or_running_ones()
    {
        await using (var context = Db.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO jobs (id, type, payload, status, attempts, run_after, created_at, last_error) VALUES
                  ({Guid.CreateVersion7()}, 'a.pending', '[]', 'pending', 0, {now}, {now}, NULL),
                  ({Guid.CreateVersion7()}, 'b.failed', '[]', 'failed', 5, {now}, {now}, 'boom'),
                  ({Guid.CreateVersion7()}, 'c.done', '[]', 'succeeded', 1, {now}, {now}, NULL),
                  ({Guid.CreateVersion7()}, 'd.running', '[]', 'running', 1, {now}, {now}, NULL)
                """);
        }

        var response = await Api.CreateClient(Guid.CreateVersion7(), roles: "admin").GetAsync("/api/admin/jobs");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await ReadJsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("type").GetString()).ShouldBe(["b.failed", "a.pending"]);
        items[0].GetProperty("status").GetString().ShouldBe("failed");
        items[0].GetProperty("attempts").GetInt32().ShouldBe(5);
        items[0].GetProperty("lastError").GetString().ShouldBe("boom");
    }
}
