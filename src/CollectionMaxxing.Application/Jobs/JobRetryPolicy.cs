namespace CollectionMaxxing.Application.Jobs;

/// <summary>How often and how fast a failing job is retried: exponential backoff, capped.</summary>
public sealed record JobRetryPolicy
{
    public int MaxAttempts { get; init; } = 5;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromHours(1);

    /// <summary>True when attempt number <paramref name="attempts"/> (1-based) failed and another try is allowed.</summary>
    public bool ShouldRetry(int attempts) => attempts < MaxAttempts;

    /// <summary>How long to wait after attempt number <paramref name="attempts"/> (1-based) failed: base, 2x base, 4x base, up to the cap.</summary>
    public TimeSpan DelayAfter(int attempts)
    {
        var delay = BaseDelay;
        for (var i = 1; i < attempts; i++)
        {
            if (delay >= MaxDelay)
            {
                break;
            }

            delay *= 2;
        }

        return delay > MaxDelay ? MaxDelay : delay;
    }
}
