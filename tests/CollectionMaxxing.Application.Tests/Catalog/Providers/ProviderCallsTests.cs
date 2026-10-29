using CollectionMaxxing.Application.Catalog.Providers;

namespace CollectionMaxxing.Application.Tests.Catalog.Providers;

/// <summary>
/// A provider adapter gives up quietly (it answers "nothing", never throws), so whoever asked it hears about an outage through
/// a failure report: the flow that opened it sees every failure reported below it, and nothing reported anywhere else.
/// </summary>
public class ProviderCallsTests
{
    /// <summary>What an adapter does when its provider is down: reports it, after a real await, and answers nothing.</summary>
    private static async Task<int> AskADownProviderAsync()
    {
        await Task.Yield();
        ProviderCalls.ReportFailure();
        return 0;
    }

    private static async Task<int> AskAHealthyProviderAsync()
    {
        await Task.Yield();
        return 1;
    }

    [Fact]
    public async Task A_failure_reported_by_the_provider_layer_is_seen_by_the_flow_that_opened_the_report()
    {
        using var report = ProviderCalls.Observe();

        await AskADownProviderAsync();

        report.Failed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_provider_that_answered_leaves_the_report_clean()
    {
        using var report = ProviderCalls.Observe();

        await AskAHealthyProviderAsync();

        report.Failed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_reported_with_no_report_open_is_ignored()
    {
        // The adapters are also called outside any report (tests, the catalogue search): reporting there is a no-op.
        (await AskADownProviderAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_asks_that_each_open_their_own_report_see_only_their_own_failures()
    {
        // Both asks are in flight at once when the down one reports: the healthy one's report must stay clean.
        var bothAsking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        async Task<bool> AskAsync(bool down)
        {
            using var report = ProviderCalls.Observe();
            if (Interlocked.Increment(ref started) == 2)
            {
                bothAsking.SetResult();
            }

            await bothAsking.Task;
            await (down ? AskADownProviderAsync() : AskAHealthyProviderAsync());
            await Task.Yield();
            return report.Failed;
        }

        var failed = await Task.WhenAll(AskAsync(down: true), AskAsync(down: false));

        failed.ShouldBe([true, false]);
    }

    [Fact]
    public async Task A_report_opened_inside_an_async_method_does_not_flow_back_to_its_caller()
    {
        ProviderFailureReport? inner = null;

        async Task OpenWithoutClosingAsync()
        {
            await Task.Yield();
            inner = ProviderCalls.Observe(); // left open on purpose: only the async-local scope ends it
        }

        await OpenWithoutClosingAsync();
        ProviderCalls.ReportFailure();

        inner.ShouldNotBeNull();
        inner.Failed.ShouldBeFalse();
        inner.Dispose();
    }

    [Fact]
    public async Task A_failure_inside_a_nested_report_is_seen_by_the_report_around_it_too()
    {
        using var outer = ProviderCalls.Observe();
        using (var inner = ProviderCalls.Observe())
        {
            await AskADownProviderAsync();
            inner.Failed.ShouldBeTrue();
        }

        outer.Failed.ShouldBeTrue();
    }

    [Fact]
    public async Task Closing_a_nested_report_hands_reporting_back_to_the_one_around_it()
    {
        using var outer = ProviderCalls.Observe();
        var inner = ProviderCalls.Observe();
        inner.Dispose();

        await AskADownProviderAsync();

        outer.Failed.ShouldBeTrue();
        inner.Failed.ShouldBeFalse();
    }
}
