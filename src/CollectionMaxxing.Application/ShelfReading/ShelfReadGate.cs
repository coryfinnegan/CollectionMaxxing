namespace CollectionMaxxing.Application.ShelfReading;

/// <summary>Whether a shelf reader is set up on this host (an endpoint is configured and its settings are valid). The phone uploads a shelf snap only when it is.</summary>
public interface IShelfReaderSetup
{
    bool IsSetUp { get; }
}

/// <summary><c>ShelfReader:QueueWaitSeconds</c> and <c>ShelfReader:QueueMaxWaiting</c>: how the requests that want the one graphics card queue.</summary>
public sealed class ShelfReadQueueOptions
{
    /// <summary>The longest a request waits for its turn before it is told the reader is busy.</summary>
    public int QueueWaitSeconds { get; set; } = 45;

    /// <summary>The most requests that may wait; the next one is told the reader is busy at once.</summary>
    public int QueueMaxWaiting { get; set; } = 3;
}

/// <summary>
/// One shelf read at a time: the card behind the reader does one picture at a time, so a second request waits its turn. At most
/// <see cref="ShelfReadQueueOptions.QueueMaxWaiting"/> wait, each for at most <see cref="ShelfReadQueueOptions.QueueWaitSeconds"/>.
/// A request that stops waiting (it is dropped) leaves the queue. A singleton, in front of <see cref="IShelfReader"/>.
/// </summary>
public sealed class ShelfReadGate(Microsoft.Extensions.Options.IOptions<ShelfReadQueueOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private int _waiting;

    /// <summary>
    /// Waits for the reader's turn. Null when the queue is full or the wait ran out (the reader is busy); otherwise the turn, to be
    /// disposed when the read is over. Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled, in the queue or not.
    /// </summary>
    public async Task<IDisposable?> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_turn.Wait(0, CancellationToken.None))
        {
            return new Turn(_turn);
        }

        var settings = options.Value;
        if (Interlocked.Increment(ref _waiting) > Math.Max(0, settings.QueueMaxWaiting))
        {
            Interlocked.Decrement(ref _waiting);
            return null;
        }

        try
        {
            return await _turn.WaitAsync(TimeSpan.FromSeconds(Math.Max(0, settings.QueueWaitSeconds)), cancellationToken)
                ? new Turn(_turn)
                : null;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>
    /// Waits for the reader's turn for as long as it takes, outside the queue's limits: the eval's re-ground reads its photos through here,
    /// one turn per photo, so a person's read never runs beside one and waits at most for the photo being read. It takes no place in the
    /// queue, so it never makes a person's request be told the reader is busy. Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public async Task<IDisposable> WaitTurnAsync(CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken);
        return new Turn(_turn);
    }

    public void Dispose() => _turn.Dispose();

    private sealed class Turn(SemaphoreSlim turn) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                turn.Release();
            }
        }
    }
}
