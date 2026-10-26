using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Quota;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Quota;

// Issue #339 (design §12): with the UPCitemdb quota stubbed at zero, capture continues — codes become
// barcode-only provisionals ("UPC … — lookup pending", digit-level duplicate-checked) — and the drain
// (nightly + session-reopen) completes them when the quota resets. No dead ends, no drops.
public class QuotaZeroTests(ApiFixture api) : UpcScanTest(api, dailyLimit: 2)
{
    private async Task StubQuotaSpentAsync()
    {
        var today = DateOnly.FromDateTime(Clock.GetUtcNow().UtcDateTime);
        await using var context = Db.CreateContext();
        await context.Database.ExecuteSqlAsync(
            $"insert into provider_quota (provider, day, used, \"limit\") values ({"upcitemdb"}, {today}, 2, 2) on conflict (provider, day) do update set used = 2, \"limit\" = 2");
    }

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

    private static string? PendingReason(System.Text.Json.JsonElement candidate) =>
        candidate.TryGetProperty("pendingReason", out var reason) && reason.ValueKind == System.Text.Json.JsonValueKind.String ? reason.GetString() : null;

    [Fact]
    public async Task WithQuotaAtZeroCaptureContinuesAsCountedProvisionalsAndTheResetDrainCompletesThem()
    {
        var library = await CreateLibraryAsync();
        var (first, second) = (ScanData.Upc(6401), ScanData.Upc(6402));
        KnowProduct(first, "Excitebike 64");
        KnowProduct(second, "Wave Race 64");
        await StubQuotaSpentAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var a = (await ScanAsync(editor, sessionId, first))[0];
        var b = (await ScanAsync(editor, sessionId, second))[0];

        Resolution(a).ShouldBe("pending");
        PendingReason(a).ShouldBe("quota");
        a.GetProperty("visibleText").GetString().ShouldBe($"UPC {ScanData.Gtin13(first)} — lookup pending");
        Resolution(b).ShouldBe("pending");
        PendingReason(b).ShouldBe("quota");
        UpcRequests.ShouldBe(0, "a spent quota makes no call");

        var again = (await ScanAsync(editor, sessionId, first))[0];
        Id(again).ShouldBe(Id(a), "the same digits are the same provisional, not a second row");

        var fetched = await GetSessionAsync(editor, sessionId);
        fetched.GetProperty("candidates").EnumerateArray().Count(PendingReasonIsQuota).ShouldBe(2);

        Clock.SetUtcNow(UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow()).AddHours(1));
        await DrainAsync();

        var after = await GetSessionAsync(editor, sessionId);
        after.GetProperty("candidates").EnumerateArray().Select(Resolution).ToList().ShouldBe(["matched", "matched"]);
        UpcRequests.ShouldBe(2);
    }

    [Fact]
    public async Task ReopeningASessionAfterTheResetRequeuesItsQuotaPendingCodes()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6411);
        KnowProduct(code, "1080 Snowboarding");
        await StubQuotaSpentAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var waiting = (await ScanAsync(editor, sessionId, code))[0];
        Resolution(waiting).ShouldBe("pending");
        (await JobsFor(Id(waiting))).Count.ShouldBe(2, "the safety net and the wait-for-reset job");

        // Two days on: the wait-for-reset job never ran, and its day has passed, so reopening re-queues the code
        // (reopening on the job's own day would find it still queued and add nothing: one job per code per day).
        Clock.SetUtcNow(UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow()).AddDays(1).AddHours(1));
        await GetSessionAsync(editor, sessionId);

        (await JobsFor(Id(waiting))).Count.ShouldBe(3, "reopening re-queues the waiting code");
        await GetSessionAsync(editor, sessionId);
        (await JobsFor(Id(waiting))).Count.ShouldBe(3, "one reopen-drain job per code per day");

        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResolvePendingCandidate>().HandleAsync(
                new SubmitBarcodes.ResolveJob(library.Id, sessionId, Id(waiting), library.EditorId), CancellationToken.None);
        }

        var after = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(after).ShouldBe("matched");
        PendingReason(after).ShouldBeNull();
    }

    [Fact]
    public async Task ALearnedBarcodeResolvesWithZeroProviderCallsEvenWhenTheQuotaIsZero()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6421);
        await CatalogAsync("Diddy Kong Racing", code);
        await StubQuotaSpentAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var again = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(again).ShouldBe("matched", "the learned layer answers first, quota or no quota");
        UpcRequests.ShouldBe(0);
    }

    private static bool PendingReasonIsQuota(System.Text.Json.JsonElement candidate) => PendingReason(candidate) == "quota";
}
