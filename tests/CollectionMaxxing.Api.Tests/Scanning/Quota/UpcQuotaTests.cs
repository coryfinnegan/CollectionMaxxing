using CollectionMaxxing.Api.Tests.Scanning.Events;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Application.Scanning.Quota;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Quota;

public class UpcQuotaTests(ApiFixture api) : UpcScanTest(api, dailyLimit: 1)
{
    private async Task DrainAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DrainQuotaBacklog>().HandleAsync(new DrainQuotaBacklog.Job(), CancellationToken.None);
    }

    private async Task<List<DateTimeOffset>> JobsFor(Guid candidateId)
    {
        var pattern = $"%{candidateId}%";
        await using var context = Db.CreateContext();
        return await context.Database.SqlQuery<DateTimeOffset>($"select run_after as \"Value\" from jobs where payload::text like {pattern}").ToListAsync();
    }

    [Fact]
    public async Task A_spent_quota_makes_the_code_pending_with_no_provider_call_and_a_job_for_after_the_reset()
    {
        var library = await CreateLibraryAsync();
        var (first, second) = (ScanData.Upc(6101), ScanData.Upc(6102));
        KnowProduct(first, "Star Fox 64");
        KnowProduct(second, "Pilotwings 64");
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var one = await ScanAsync(editor, sessionId, first); // spends the day's only lookup
        Resolution(one[0]).ShouldBe("matched");
        UpcRequests.ShouldBe(1);

        var two = await ScanAsync(editor, sessionId, second);

        Resolution(two[0]).ShouldBe("pending");
        UpcRequests.ShouldBe(1, "a spent quota makes no call");
        var jobs = await JobsFor(Id(two[0]));
        jobs.Count.ShouldBe(1);
        jobs[0].ShouldBe(UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow()));
    }

    [Fact]
    public async Task The_nightly_job_resolves_the_code_the_next_day_and_emits_candidate_updated()
    {
        var library = await CreateLibraryAsync();
        var (first, second) = (ScanData.Upc(6111), ScanData.Upc(6112));
        KnowProduct(first, "Kirby 64");
        KnowProduct(second, "Banjo-Kazooie");
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, first);
        var waiting = (await ScanAsync(editor, sessionId, second))[0];
        Resolution(waiting).ShouldBe("pending");
        await using var events = await SseClient.ConnectAsync(editor, SessionUrl(sessionId) + "/events");
        await events.ReadUntilAsync("session.status");

        await DrainAsync(); // still the same day: the quota is still spent, nothing changes
        Resolution((await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[1]).ShouldBe("pending");
        UpcRequests.ShouldBe(1);

        Clock.SetUtcNow(UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow()).AddHours(1));
        await DrainAsync();

        UpcRequests.ShouldBe(2);
        var updated = (await events.ReadUntilAsync(
            "candidate.updated", e => e.Data!.Value.GetProperty("resolution").GetString() == "matched")).Last();
        updated.Data!.Value.GetProperty("id").GetGuid().ShouldBe(Id(waiting));
        Decision((await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[1]).ShouldBe("accepted");
    }

    [Fact]
    public async Task With_quota_partly_left_the_nightly_job_drains_only_what_is_allowed_oldest_first()
    {
        var library = await CreateLibraryAsync();
        var codes = new[] { ScanData.Upc(6121), ScanData.Upc(6122), ScanData.Upc(6123), ScanData.Upc(6124) };
        var titles = new[] { "Mario Kart 64", "F-Zero X", "Goldeneye 007", "Paper Mario" };
        for (var i = 0; i < codes.Length; i++)
        {
            KnowProduct(codes[i], titles[i]);
        }

        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, codes[0]); // spends today's lookup
        foreach (var code in codes.Skip(1))
        {
            Resolution((await ScanAsync(editor, sessionId, code))[0]).ShouldBe("pending"); // one by one, so their order is fixed
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Clock.SetUtcNow(UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow()).AddHours(1));
        await DrainAsync();

        UpcRequests.ShouldBe(2, "one lookup today, one tomorrow: the allowance is one a day");
        var resolutions = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates").EnumerateArray().Select(Resolution).ToList();
        resolutions.ShouldBe(["matched", "matched", "pending", "pending"]);

        Clock.SetUtcNow(UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow()).AddHours(1));
        await DrainAsync();

        UpcRequests.ShouldBe(3);
        (await GetSessionAsync(editor, sessionId)).GetProperty("candidates").EnumerateArray().Select(Resolution).ToList()
            .ShouldBe(["matched", "matched", "matched", "pending"]);
    }

    [Fact]
    public async Task The_per_code_job_waits_another_day_instead_of_giving_up_when_the_quota_is_spent_again()
    {
        var library = await CreateLibraryAsync();
        var (first, second) = (ScanData.Upc(6131), ScanData.Upc(6132));
        KnowProduct(first, "Yoshi's Story");
        KnowProduct(second, "Mario Party");
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, first);
        var waiting = (await ScanAsync(editor, sessionId, second))[0];

        // The same day the quota is still spent: running the code's own job must keep it pending, not turn it into an error.
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResolvePendingCandidate>().HandleAsync(
                new SubmitBarcodes.ResolveJob(library.Id, sessionId, Id(waiting), library.EditorId), CancellationToken.None);
        }

        Resolution((await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[1]).ShouldBe("pending");
        UpcRequests.ShouldBe(1);
        (await JobsFor(Id(waiting))).ShouldAllBe(at => at == UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow()));
    }

    [Fact]
    public void The_drain_and_the_cleanup_are_recurring_jobs_that_run_once_a_day()
    {
        var recurring = Factory.Services.GetServices<RecurringJobRegistration>().ToList();

        recurring.ShouldContain(r => r.Type == DrainQuotaBacklog.JobType && r.Every == TimeSpan.FromDays(1));
    }
}
