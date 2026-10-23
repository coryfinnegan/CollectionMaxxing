namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Makes the first calls of one identity take turns (per process): when a phone sends several at once, the first makes the account and the
/// others find it, instead of each asking Auth0 and each using a permit of the "new accounts" budget (<see cref="AccountLimits"/>). Calls of
/// different identities don't wait for each other. A turn is forgotten when nobody is waiting for it.
/// </summary>
public sealed class NewAccountTurns
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Turn> _turns = [];

    /// <summary>Waits for this identity's turn. Dispose the result to give the turn to the next call.</summary>
    public async Task<IDisposable> WaitAsync(string subject, CancellationToken cancellationToken)
    {
        Turn turn;
        lock (_gate)
        {
            if (!_turns.TryGetValue(subject, out turn!))
            {
                turn = _turns[subject] = new Turn();
            }

            turn.Waiting++;
        }

        try
        {
            await turn.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(subject, turn, held: false);
            throw;
        }

        return new Release(this, subject, turn);
    }

    private void Leave(string subject, Turn turn, bool held)
    {
        if (held)
        {
            turn.Semaphore.Release();
        }

        lock (_gate)
        {
            if (--turn.Waiting == 0)
            {
                _turns.Remove(subject);
            }
        }
    }

    private sealed class Turn
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Waiting { get; set; }
    }

    private sealed class Release(NewAccountTurns owner, string subject, Turn turn) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Leave(subject, turn, held: true);
            }
        }
    }
}
