using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>
/// The memo is shared by lookups that run at the same time: one read asked twice at once is grounded once and both asks get the
/// answer; a grounding that fails leaves nothing behind; the asks that were waiting on a cancelled grounding ask for themselves.
/// </summary>
public class EvalGroundingMemoConcurrencyTests
{
    private const ScoringProfileKind Profile = ScoringProfileKind.LooseCartridge;

    private static ReadFacts Facts(string title = "Lantern Keep") =>
        new(title, title, null, null, ItemCompleteness.CartOnly, WorkKind.Game, ReleaseFormat.GameCartridge, null, "Nintendo 64");

    private static BarcodeResolution Answer(ResolutionVerdict verdict = ResolutionVerdict.Matched) => new(verdict, [], null, []);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_read_asked_again_while_it_is_being_grounded_waits_for_that_grounding()
    {
        var memo = new EvalGroundingMemo();
        var open = new TaskCompletionSource();
        var calls = 0;
        var answer = Answer();

        async Task<BarcodeResolution> Slow()
        {
            Interlocked.Increment(ref calls);
            await open.Task;
            return answer;
        }

        var first = memo.GroundAsync(Facts(), Profile, Slow);
        var second = memo.GroundAsync(Facts(), Profile, Slow);

        second.IsCompleted.ShouldBeFalse();
        open.SetResult();
        (await first.WaitAsync(Patience)).ShouldBeSameAs(answer);
        (await second.WaitAsync(Patience)).ShouldBeSameAs(answer);
        calls.ShouldBe(1);
        memo.Grounded.ShouldBe(1);
        memo.Reused.ShouldBe(1);
    }

    [Fact]
    public async Task Many_asks_for_one_read_from_many_threads_ground_it_once()
    {
        var memo = new EvalGroundingMemo();
        var calls = 0;

        async Task<BarcodeResolution> Ground()
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(20);
            return Answer();
        }

        var answers = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => memo.GroundAsync(Facts(), Profile, Ground))))
            .WaitAsync(Patience);

        calls.ShouldBe(1);
        answers.Distinct().Count().ShouldBe(1);
        memo.Grounded.ShouldBe(1);
        memo.Reused.ShouldBe(63);
    }

    [Fact]
    public async Task A_grounding_that_fails_fails_the_ask_waiting_on_it_and_is_asked_again_next_time()
    {
        var memo = new EvalGroundingMemo();
        var open = new TaskCompletionSource();

        var first = memo.GroundAsync(Facts(), Profile, async () =>
        {
            await open.Task;
            throw new InvalidOperationException("the catalogue is down");
        });
        var second = memo.GroundAsync(Facts(), Profile, () => Task.FromResult(Answer()));
        open.SetResult();

        await Should.ThrowAsync<InvalidOperationException>(() => first.WaitAsync(Patience));
        await Should.ThrowAsync<InvalidOperationException>(() => second.WaitAsync(Patience));
        var next = await memo.GroundAsync(Facts(), Profile, () => Task.FromResult(Answer(ResolutionVerdict.NotFound)));

        next.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        memo.Grounded.ShouldBe(1);
        memo.Reused.ShouldBe(0);
    }

    [Fact]
    public async Task An_ask_waiting_on_a_grounding_that_was_cancelled_grounds_the_read_itself()
    {
        var memo = new EvalGroundingMemo();
        using var cancelFirst = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var mine = Answer(ResolutionVerdict.Ambiguous);

        var first = memo.GroundAsync(Facts(), Profile, async () =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancelFirst.Token);
            return Answer();
        }, cancelFirst.Token);
        await started.Task.WaitAsync(Patience);
        var second = memo.GroundAsync(Facts(), Profile, () => Task.FromResult(mine), CancellationToken.None);

        await cancelFirst.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => first.WaitAsync(Patience));
        (await second.WaitAsync(Patience)).ShouldBeSameAs(mine);
        memo.Grounded.ShouldBe(1);
        memo.Reused.ShouldBe(0);
    }

    [Fact]
    public async Task An_ask_stops_waiting_when_its_own_token_is_cancelled_and_the_grounding_goes_on()
    {
        var memo = new EvalGroundingMemo();
        var open = new TaskCompletionSource();
        var answer = Answer();
        using var cancelSecond = new CancellationTokenSource();

        var first = memo.GroundAsync(Facts(), Profile, async () =>
        {
            await open.Task;
            return answer;
        });
        var second = memo.GroundAsync(Facts(), Profile, () => Task.FromResult(Answer(ResolutionVerdict.NotFound)), cancelSecond.Token);

        await cancelSecond.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => second.WaitAsync(Patience));
        open.SetResult();

        (await first.WaitAsync(Patience)).ShouldBeSameAs(answer);
        (await memo.GroundAsync(Facts(), Profile, () => throw new InvalidOperationException("asked again"))).ShouldBeSameAs(answer);
    }

    [Fact]
    public async Task Reads_asked_together_are_answered_in_the_order_asked_and_a_repeat_among_them_is_grounded_once()
    {
        var memo = new EvalGroundingMemo();
        var grounded = new List<string>();
        var reads = new[] { Facts("Ember Tide"), Facts("Lantern Keep"), Facts("Ember Tide"), Facts("Dusk Rider") };

        var answers = await memo.GroundManyAsync(
            [.. reads.Select(r => (r, Profile))],
            claims =>
            {
                foreach (var claim in claims)
                {
                    grounded.Add(claim.Facts.Title!);
                    claim.Complete(Answer(claim.Facts.Title == "Lantern Keep" ? ResolutionVerdict.Ambiguous : ResolutionVerdict.Matched));
                }

                return Task.CompletedTask;
            });

        grounded.ShouldBe(["Ember Tide", "Lantern Keep", "Dusk Rider"]);
        answers.Select(a => a.Verdict).ShouldBe(
            [ResolutionVerdict.Matched, ResolutionVerdict.Ambiguous, ResolutionVerdict.Matched, ResolutionVerdict.Matched]);
        answers[2].ShouldBeSameAs(answers[0]);
        memo.Grounded.ShouldBe(3);
        memo.Reused.ShouldBe(1);
    }

    [Fact]
    public async Task Reads_already_grounded_are_not_handed_out_again()
    {
        var memo = new EvalGroundingMemo();
        var known = await memo.GroundAsync(Facts("Ember Tide"), Profile, () => Task.FromResult(Answer()));
        var handedOut = new List<string>();

        var answers = await memo.GroundManyAsync(
            [(Facts("Ember Tide"), Profile), (Facts("Lantern Keep"), Profile)],
            claims =>
            {
                foreach (var claim in claims)
                {
                    handedOut.Add(claim.Facts.Title!);
                    claim.Complete(Answer(ResolutionVerdict.NotFound));
                }

                return Task.CompletedTask;
            });

        handedOut.ShouldBe(["Lantern Keep"]);
        answers[0].ShouldBeSameAs(known);
        memo.Reused.ShouldBe(1);
    }

    [Fact]
    public async Task When_grounding_the_reads_fails_the_ones_it_did_not_finish_are_forgotten_and_the_ones_it_did_are_kept()
    {
        var memo = new EvalGroundingMemo();

        await Should.ThrowAsync<InvalidOperationException>(() => memo.GroundManyAsync(
            [(Facts("Ember Tide"), Profile), (Facts("Lantern Keep"), Profile)],
            claims =>
            {
                claims[0].Complete(Answer());
                throw new InvalidOperationException("the catalogue is down");
            }));

        var again = new List<string>();
        await memo.GroundManyAsync(
            [(Facts("Ember Tide"), Profile), (Facts("Lantern Keep"), Profile)],
            claims =>
            {
                foreach (var claim in claims)
                {
                    again.Add(claim.Facts.Title!);
                    claim.Complete(Answer());
                }

                return Task.CompletedTask;
            });

        again.ShouldBe(["Lantern Keep"]);
        memo.Grounded.ShouldBe(2);
    }

    [Fact]
    public async Task A_read_the_grounding_forgot_to_answer_is_an_error_and_is_asked_again_next_time()
    {
        var memo = new EvalGroundingMemo();

        await Should.ThrowAsync<InvalidOperationException>(() => memo.GroundManyAsync([(Facts(), Profile)], _ => Task.CompletedTask));

        var next = await memo.GroundAsync(Facts(), Profile, () => Task.FromResult(Answer()));
        next.Verdict.ShouldBe(ResolutionVerdict.Matched);
        memo.Grounded.ShouldBe(1);
    }
}
