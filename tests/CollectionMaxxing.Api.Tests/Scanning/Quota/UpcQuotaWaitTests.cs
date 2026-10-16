using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Quota;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Quota;

// SCAN-1d (issue #105), contract item 4: the candidate says why a code is still Pending, so Review can explain a quota wait
// ("waiting for tomorrow's lookup") instead of showing a spinner. No trace contents are exposed.
public class UpcQuotaWaitTests(ApiFixture api) : UpcScanTest(api, dailyLimit: 1)
{
    private static string? PendingReason(System.Text.Json.JsonElement candidate) =>
        candidate.TryGetProperty("pendingReason", out var reason) && reason.ValueKind == System.Text.Json.JsonValueKind.String ? reason.GetString() : null;

    private static DateTimeOffset? RetryAfter(System.Text.Json.JsonElement candidate) =>
        candidate.TryGetProperty("retryAfter", out var at) && at.ValueKind == System.Text.Json.JsonValueKind.String ? at.GetDateTimeOffset() : null;

    private async Task DrainAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DrainQuotaBacklog>().HandleAsync(new DrainQuotaBacklog.Job(), CancellationToken.None);
    }

    [Fact]
    public async Task A_code_waiting_for_the_quota_says_so_and_when_the_nightly_drain_will_try_and_after_the_drain_it_is_gone()
    {
        var library = await CreateLibraryAsync();
        var (first, second) = (ScanData.Upc(7401), ScanData.Upc(7402));
        KnowProduct(first, "Star Fox 64");
        KnowProduct(second, "Pilotwings 64");
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, first); // spends the day's only lookup

        var waiting = (await ScanAsync(editor, sessionId, second))[0];

        Resolution(waiting).ShouldBe("pending");
        PendingReason(waiting).ShouldBe("quota");
        RetryAfter(waiting).ShouldBe(UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow()));
        var fetched = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[1];
        PendingReason(fetched).ShouldBe("quota");
        RetryAfter(fetched).ShouldBe(UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow()));
        waiting.GetRawText().ShouldNotContain("product-lookup", Case.Insensitive, "no trace contents are exposed");

        var firstReset = UpcQuotaWindow.NextResetAfter(Clock.GetUtcNow());
        Clock.SetUtcNow(firstReset.AddHours(1));
        await DrainAsync();

        var after = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[1];
        Resolution(after).ShouldBe("matched");
        PendingReason(after).ShouldBeNull();
        RetryAfter(after).ShouldBeNull();
    }

    [Fact]
    public async Task A_code_that_is_pending_because_a_provider_was_down_says_lookup_with_no_retry_time()
    {
        var library = await CreateLibraryAsync();
        var unknown = ScanData.Upc(7411); // the test lookup answers "unavailable" for a code nobody stubbed
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var pending = (await ScanAsync(editor, sessionId, unknown))[0];

        Resolution(pending).ShouldBe("pending");
        PendingReason(pending).ShouldBe("lookup");
        RetryAfter(pending).ShouldBeNull();
    }

    [Fact]
    public async Task A_resolved_candidate_has_neither_a_reason_nor_a_retry_time()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(7421);
        KnowProduct(code, "Kirby 64");
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var matched = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(matched).ShouldBe("matched");
        PendingReason(matched).ShouldBeNull();
        RetryAfter(matched).ShouldBeNull();
    }
}
