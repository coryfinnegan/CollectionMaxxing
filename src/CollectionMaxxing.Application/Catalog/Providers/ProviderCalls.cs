namespace CollectionMaxxing.Application.Catalog.Providers;

/// <summary>
/// How patient the provider calls made inside a scope may be. A person waiting on a scan or a search gets an answer as soon as
/// every provider has answered or failed, so their calls are made inside <see cref="FailFast"/>: a provider that fails is given
/// up on at once, not retried with back-off until the time budget runs out. Calls made outside a scope (the background jobs,
/// which have the long budget) keep the full retry policy.
/// </summary>
public static class ProviderCalls
{
    private static readonly AsyncLocal<bool> Fast = new();

    /// <summary>True while the calling flow is inside <see cref="FailFast"/>.</summary>
    public static bool IsFailFast => Fast.Value;

    /// <summary>Marks the provider calls the current flow makes as interactive. Dispose to leave the scope.</summary>
    public static IDisposable FailFast()
    {
        var previous = Fast.Value;
        Fast.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Fast.Value = previous;
    }
}
