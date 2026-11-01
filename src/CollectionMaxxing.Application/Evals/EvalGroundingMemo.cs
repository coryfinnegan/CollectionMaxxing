using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// What grounding answered for each distinct read in one run of the evals. The same title is usually read by all three engines and by
/// several photos, and every lookup costs catalogue and provider calls, so a read that
/// repeats is answered from here. A read is "the same" when its cleaned <see cref="ReadFacts"/> (a record of plain fields, so it
/// compares by value) and its <see cref="ScoringProfileKind"/> are equal: the profile follows from the photo too, not only the read.
/// </summary>
/// <remarks>
/// Made by the handler at the start of a run and passed down, so it lives exactly as long as that run and the next run asks the
/// catalogue afresh. Safe for lookups that run at the same time: a read is claimed by the first ask for it, and an identical read
/// asked meanwhile, in the same call or another, waits for that grounding instead of starting its own. A grounding that fails or is
/// cancelled leaves nothing behind, so the next ask for the read grounds it afresh.
/// </remarks>
public sealed class EvalGroundingMemo
{
    private readonly object _gate = new();
    private readonly Dictionary<(ReadFacts Facts, ScoringProfileKind Profile), Claim> _claims = [];
    private int _grounded;
    private int _reused;

    /// <summary>How many distinct reads were really grounded.</summary>
    public int Grounded => Volatile.Read(ref _grounded);

    /// <summary>How many times a read was answered from what an earlier read had already found, or was finding.</summary>
    public int Reused => Volatile.Read(ref _reused);

    /// <summary>
    /// The answer for the read: the remembered one, else <paramref name="ground"/>'s, which is then remembered. A read already being
    /// grounded by another ask is waited for, not grounded twice. A grounding that throws leaves nothing behind, and an ask waiting on
    /// a grounding that was cancelled grounds the read itself. <paramref name="cancellationToken"/> only ends this ask's wait.
    /// </summary>
    public async Task<BarcodeResolution> GroundAsync(
        ReadFacts facts, ScoringProfileKind profile, Func<Task<BarcodeResolution>> ground, CancellationToken cancellationToken = default)
    {
        var answers = await GroundManyAsync([(facts, profile)], async claims => claims[0].Complete(await ground()), cancellationToken);
        return answers[0];
    }

    /// <summary>
    /// The answers for the reads, in the order given. The reads nobody has claimed (each distinct one once) are claimed by this call
    /// and handed to <paramref name="groundThem"/>, which must <see cref="Claim.Complete"/> each; the rest are the remembered
    /// answers, or those being grounded by other asks, which are waited for. When <paramref name="groundThem"/> throws, the claims
    /// it had not completed are forgotten (the ones it had stay remembered) and the exception goes on to the caller.
    /// </summary>
    public async Task<IReadOnlyList<BarcodeResolution>> GroundManyAsync(
        IReadOnlyList<(ReadFacts Facts, ScoringProfileKind Profile)> reads,
        Func<IReadOnlyList<Claim>, Task> groundThem,
        CancellationToken cancellationToken = default)
    {
        var answers = new BarcodeResolution[reads.Count];
        var waiting = Enumerable.Range(0, reads.Count).ToList();
        while (waiting.Count > 0)
        {
            var held = waiting.Select(i => (Index: i, Held: Hold(reads[i].Facts, reads[i].Profile))).ToList();
            await GroundOwnAsync([.. held.Where(h => h.Held.Mine).Select(h => h.Held.Claim)], groundThem);

            waiting = [];
            foreach (var (index, (claim, mine)) in held)
            {
                try
                {
                    answers[index] = await claim.Answer.WaitAsync(cancellationToken);
                    if (!mine)
                    {
                        Interlocked.Increment(ref _reused);
                    }
                }
                catch (OperationCanceledException) when (!mine && !cancellationToken.IsCancellationRequested)
                {
                    waiting.Add(index); // the ask that was grounding it was cancelled, not this one: ground it afresh
                }
            }
        }

        return answers;
    }

    private static async Task GroundOwnAsync(List<Claim> own, Func<IReadOnlyList<Claim>, Task> groundThem)
    {
        if (own.Count == 0)
        {
            return;
        }

        try
        {
            await groundThem(own);
        }
        catch (Exception exception)
        {
            foreach (var claim in own)
            {
                claim.Fail(exception);
            }

            throw;
        }

        var unanswered = new InvalidOperationException("Grounding returned without answering every read it was given.");
        foreach (var claim in own)
        {
            claim.Fail(unanswered);
        }
    }

    private (Claim Claim, bool Mine) Hold(ReadFacts facts, ScoringProfileKind profile)
    {
        lock (_gate)
        {
            if (_claims.TryGetValue((facts, profile), out var held))
            {
                return (held, false);
            }

            var claim = new Claim(this, facts, profile);
            _claims[(facts, profile)] = claim;
            return (claim, true);
        }
    }

    /// <summary>One distinct read that an ask has taken on grounding. Its answer is what the asks waiting for the same read receive.</summary>
    public sealed class Claim
    {
        private readonly EvalGroundingMemo _memo;
        private readonly TaskCompletionSource<BarcodeResolution> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Claim(EvalGroundingMemo memo, ReadFacts facts, ScoringProfileKind profile)
        {
            _memo = memo;
            Facts = facts;
            Profile = profile;
        }

        public ReadFacts Facts { get; }

        public ScoringProfileKind Profile { get; }

        internal Task<BarcodeResolution> Answer => _answer.Task;

        /// <summary>Remembers the answer and hands it to every ask waiting for this read. Safe to call from any thread; the first answer stands.</summary>
        public void Complete(BarcodeResolution answer)
        {
            lock (_memo._gate)
            {
                if (_answer.Task.IsCompleted)
                {
                    return;
                }

                _answer.SetResult(answer);
                _memo._grounded++;
            }
        }

        /// <summary>Forgets this read, unless it was answered, so the next ask grounds it afresh; the asks waiting for it get the failure.</summary>
        internal void Fail(Exception exception)
        {
            lock (_memo._gate)
            {
                if (_answer.Task.IsCompleted)
                {
                    return;
                }

                _memo._claims.Remove((Facts, Profile));
                if (exception is OperationCanceledException cancelled)
                {
                    _answer.SetCanceled(cancelled.CancellationToken);
                }
                else
                {
                    _answer.SetException(exception);
                    _ = _answer.Task.Exception; // nobody may be waiting for it; a failure no one reads is not an unobserved one
                }
            }
        }
    }
}
