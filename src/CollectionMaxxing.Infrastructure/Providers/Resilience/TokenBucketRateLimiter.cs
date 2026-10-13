using System.Collections.Concurrent;

namespace CollectionMaxxing.Infrastructure.Providers.Resilience;

/// <summary>
/// A token bucket holding one token that refills every <c>interval</c>: calls are spaced at least that far apart, so a
/// provider limited to 3 requests a second never sees more than 3 in any second. Waiting uses the injected clock.
/// </summary>
public sealed class TokenBucketRateLimiter
{
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DateTimeOffset _nextFree = DateTimeOffset.MinValue;

    public TokenBucketRateLimiter(TimeSpan interval, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        _interval = interval;
        _time = time;
    }

    /// <summary>The limiter for a provider allowed <paramref name="requestsPerSecond"/> calls a second.</summary>
    public static TokenBucketRateLimiter PerSecond(double requestsPerSecond, TimeProvider time) =>
        new(IntervalFor(requestsPerSecond), time);

    /// <summary>The gap between calls for a rate, rounded up so the rate is never exceeded.</summary>
    public static TimeSpan IntervalFor(double requestsPerSecond) =>
        TimeSpan.FromTicks((long)Math.Ceiling(TimeSpan.TicksPerSecond / requestsPerSecond));

    /// <summary>Takes the next free slot and says how long the caller must wait before using it.</summary>
    public TimeSpan Reserve()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var start = now > _nextFree ? now : _nextFree;
            _nextFree = start + _interval;
            return start - now;
        }
    }

    /// <summary>Completes when the caller may send its request.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        var delay = Reserve();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, ct);
        }
    }
}

/// <summary>One limiter per provider, shared by every request that provider makes.</summary>
public sealed class ProviderRateLimiters(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _limiters = new();

    public TokenBucketRateLimiter For(string provider, TimeSpan interval) =>
        _limiters.GetOrAdd(provider, _ => new TokenBucketRateLimiter(interval, time));
}
