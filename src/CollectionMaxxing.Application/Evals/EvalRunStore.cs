using System.Collections.Concurrent;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// Where the PC's one run lives until it is finished: on disk under the results folder, so a redeploy loses nothing. The folder is also the
/// queue of the background work: a photo that is stored and has no grounded file yet is waiting to be grounded. Photo ids are checked
/// (<see cref="EvalIds"/>) before they get here. Every method may throw <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
/// when the folder cannot be written; a file is written whole to a temp name and moved into place, so nothing is left half written.
/// </summary>
public interface IEvalRunStore
{
    /// <summary>True when <c>Evals:ResultsPath</c> is set and the evals are on.</summary>
    bool IsConfigured { get; }

    /// <summary>The run the PC holds, or null when there is none (or its header cannot be read).</summary>
    OpenRunHeader? Load();

    /// <summary>Throws away whatever run is stored and starts this one with no photos.</summary>
    void Create(OpenRunHeader header);

    /// <summary>Replaces the stored run's header.</summary>
    void Save(OpenRunHeader header);

    /// <summary>Removes the run and every file stored for it.</summary>
    void Discard();

    /// <summary>The ids of the photos the PC holds.</summary>
    IReadOnlySet<string> StoredIds();

    /// <summary>The ids of the photos that have a grounded file.</summary>
    IReadOnlySet<string> GroundedIds();

    /// <summary>The stored photo, or null when there is none or it cannot be read.</summary>
    StoredEvalPhoto? LoadPhoto(string photoId);

    /// <summary>Stores the photo, replacing one stored before.</summary>
    void SavePhoto(string photoId, StoredEvalPhoto photo);

    /// <summary>Removes the stored photo, if there is one.</summary>
    void DeletePhoto(string photoId);

    /// <summary>The grounded photo, or null when there is none or it cannot be read.</summary>
    GroundedPhotoFile? LoadGrounded(string photoId);

    /// <summary>Keeps what grounding made of the photo, replacing an earlier one.</summary>
    void SaveGrounded(string photoId, GroundedPhotoFile grounded);

    /// <summary>Removes the grounded file, if there is one, so the photo is grounded again.</summary>
    void DeleteGrounded(string photoId);

    /// <summary>Removes the stored and grounded photos, keeping the header (a scored run needs neither: its results are in the result files).</summary>
    void ClearPhotos();
}

/// <summary>
/// What the API process shares between the requests that change the open run and the background worker: one gate so they never write the run at the
/// same time, a count per photo that tells the worker its photo was replaced while it was grounding it, and a signal that wakes the worker when there
/// is something to do. Memory only: after a restart the files are all there is, and nothing is in flight.
/// </summary>
public sealed class EvalRunCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);
    private CancellationTokenSource _opened = new();
    private int _disposed;

    /// <summary>
    /// Cancelled when a phone run is opened (see <see cref="RunOpened"/>): background work that must never hold a phone run up, like grounding the kept
    /// readings of the last one again, links this to its own token. Take it before looking whether a run is open, so an open that follows is not missed.
    /// </summary>
    public CancellationToken RunOpenedToken
    {
        get
        {
            while (true)
            {
                try
                {
                    return Volatile.Read(ref _opened).Token;
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
                {
                    // A run opened between reading the source and asking it for its token, and the source was cancelled and disposed: read the new one.
                }
            }
        }
    }

    /// <summary>Waits for the gate. The caller holds it until the returned object is disposed; hold it only for short, local work.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    /// <summary>Notes that the photo was stored again. Call with the gate held.</summary>
    public void Replaced(string photoId) => _versions.AddOrUpdate(photoId, 1, (_, count) => count + 1);

    /// <summary>How many times the photo was stored in this process. Compare before and after grounding it.</summary>
    public long VersionOf(string photoId) => _versions.TryGetValue(photoId, out var count) ? count : 0;

    /// <summary>Forgets every photo's count (a run was opened or discarded). Call with the gate held.</summary>
    public void Forget() => _versions.Clear();

    /// <summary>Notes that a phone run was opened: whatever holds a token from before (<see cref="RunOpenedToken"/>) is asked to stop, and the next token is a fresh one.</summary>
    public void RunOpened()
    {
        var previous = Interlocked.Exchange(ref _opened, new CancellationTokenSource());

        // Cancelling runs the callbacks, and often the code that was waiting on them, on the calling thread. The caller holds the gate, so that happens on another.
        _ = Task.Run(() =>
        {
            try
            {
                previous.Cancel();
            }
            finally
            {
                previous.Dispose(); // nothing takes a token from it any more: the new source is the one in the field
            }
        });
    }

    /// <summary>Tells the worker there is something to do.</summary>
    public void Wake()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already woken: that is all the signal says.
            }
        }
    }

    /// <summary>Waits until the worker is woken or <paramref name="longest"/> has passed on <paramref name="time"/>, whichever comes first.</summary>
    public async Task WaitForWorkAsync(TimeSpan longest, TimeProvider time, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(longest, time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _signal.WaitAsync(either.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The wait ran out: look anyway.
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, 1);
        _gate.Dispose();
        _signal.Dispose();
        _opened.Dispose();
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
