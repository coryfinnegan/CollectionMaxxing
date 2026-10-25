using System.Collections.Concurrent;

namespace CollectionMaxxing.Infrastructure.Providers.Resilience;

/// <summary>
/// Coalesces concurrent identical provider lookups ("singleflight"): callers asking for the same provider and cache key
/// at the same time share one upstream call instead of each spending quota and calling the provider. The first caller
/// runs the whole fetch (cache check, quota admission, HTTP, cache write); the rest wait and then see what it cached.
/// </summary>
public sealed class ProviderRequestCoalescer
{
    private readonly ConcurrentDictionary<string, KeyLock> _locks = new();

    public async Task<T> RunAsync<T>(string key, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var keyLock = _locks.GetOrAdd(key, static _ => new KeyLock());
        Interlocked.Increment(ref keyLock.Waiters);
        try
        {
            await keyLock.Gate.WaitAsync(ct);
            try
            {
                return await action(ct);
            }
            finally
            {
                keyLock.Gate.Release();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref keyLock.Waiters) == 0)
            {
                // Only this instance goes: a newcomer that arrived mid-removal keeps its own lock.
                _locks.TryRemove(new KeyValuePair<string, KeyLock>(key, keyLock));
            }
        }
    }

    // A removed lock is never disposed: a waiter that fetched it a moment ago may still enter it, and a disposed
    // semaphore would throw under them. SemaphoreSlim holds no unmanaged resources until its wait handle is used,
    // which never happens here, so the collector reclaims removed locks.
#pragma warning disable CA1001 // Transient per-key locks are reclaimed, never disposed, by design (see above).
    private sealed class KeyLock
#pragma warning restore CA1001
    {
        public readonly SemaphoreSlim Gate = new(1, 1);

        public int Waiters;
    }
}
