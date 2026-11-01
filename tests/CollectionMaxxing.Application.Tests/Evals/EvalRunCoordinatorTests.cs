using CollectionMaxxing.Application.Evals;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>
/// The coordinator tells background work that a phone run was opened, so a re-ground of the last run's readings gives way: a token taken before the
/// open is cancelled by it, and a token taken after is a fresh one.
/// </summary>
public sealed class EvalRunCoordinatorTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_token_taken_before_a_run_opens_is_cancelled_by_it_and_the_next_token_is_not()
    {
        using var coordinator = new EvalRunCoordinator();
        var before = coordinator.RunOpenedToken;
        before.IsCancellationRequested.ShouldBeFalse();

        coordinator.RunOpened();

        await WaitUntilCancelledAsync(before);
        coordinator.RunOpenedToken.IsCancellationRequested.ShouldBeFalse("work that starts after the run opened is not asked to stop");
    }

    [Fact]
    public async Task Something_waiting_on_a_token_stops_when_a_run_opens()
    {
        using var coordinator = new EvalRunCoordinator();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None, coordinator.RunOpenedToken);
        var waiting = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);

        coordinator.RunOpened();

        await Should.ThrowAsync<TaskCanceledException>(() => waiting.WaitAsync(Bounded));
    }

    [Fact]
    public async Task Tokens_can_be_taken_while_runs_are_opened_over_and_over_without_ever_meeting_a_disposed_source()
    {
        using var coordinator = new EvalRunCoordinator();
        using var stop = new CancellationTokenSource();
        var opening = Task.Run(
            () =>
            {
                for (var i = 0; i < 20_000; i++)
                {
                    coordinator.RunOpened();
                }

                stop.Cancel();
            },
            CancellationToken.None);

        var taken = 0;
        while (!stop.IsCancellationRequested)
        {
            _ = coordinator.RunOpenedToken.IsCancellationRequested; // would throw if it handed out the token of a source already disposed
            taken++;
        }

        await opening;
        taken.ShouldBeGreaterThan(0);
    }

    private static async Task WaitUntilCancelledAsync(CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(Bounded);
        await Should.NotThrowAsync(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        });
    }
}
