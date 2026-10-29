namespace CollectionMaxxing.Application.Catalog.Providers;

/// <summary>
/// How patient the provider calls made inside a scope may be. A person waiting on a scan or a search gets an answer as soon as
/// every provider has answered or failed, so their calls are made inside <see cref="FailFast"/>: a provider that fails is given
/// up on at once, not retried with back-off until the time budget runs out. Calls made outside a scope (the background jobs,
/// which have the long budget) keep the full retry policy.
/// </summary>
/// <remarks>
/// It also carries what became of those calls. An adapter that cannot reach its provider gives up quietly: it answers
/// "nothing", never throws. Whoever asked opens a report with <see cref="Observe"/> around one ask, and the provider layer
/// marks it with <see cref="ReportFailure"/>, so a read asked during an outage can be asked again instead of being called
/// unknown. A report is async-local: it sees the failures of the flow that opened it and of the work it awaits, never those of
/// a concurrent ask, so each concurrent ask opens its own.
/// </remarks>
public static class ProviderCalls
{
    private static readonly AsyncLocal<bool> Fast = new();

    private static readonly AsyncLocal<ProviderFailureReport?> Report = new();

    /// <summary>True while the calling flow is inside <see cref="FailFast"/>.</summary>
    public static bool IsFailFast => Fast.Value;

    /// <summary>Marks the provider calls the current flow makes as interactive. Dispose to leave the scope.</summary>
    public static IDisposable FailFast()
    {
        var previous = Fast.Value;
        Fast.Value = true;
        return new Scope(previous);
    }

    /// <summary>Opens a failure report for the provider calls the current flow makes from here on. Dispose to close it.</summary>
    public static ProviderFailureReport Observe()
    {
        var report = new ProviderFailureReport(Report.Value);
        Report.Value = report;
        return report;
    }

    internal static void Close(ProviderFailureReport report) => Report.Value = report.Outer;

    /// <summary>
    /// Records that a provider call could not be answered (the provider was down, slow, unreachable or sent an unusable answer;
    /// not "it does not know that", not a spent quota). Marks the open report and every report around it; does nothing outside one.
    /// </summary>
    public static void ReportFailure()
    {
        for (var report = Report.Value; report is not null; report = report.Outer)
        {
            report.MarkFailed();
        }
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Fast.Value = previous;
    }
}

/// <summary>Whether a provider call made since <see cref="ProviderCalls.Observe"/> could not be answered.</summary>
public sealed class ProviderFailureReport : IDisposable
{
    private volatile bool _failed;

    internal ProviderFailureReport(ProviderFailureReport? outer) => Outer = outer;

    /// <summary>True when a provider call was given up on: the empty answer the caller got means "not asked", not "not known".</summary>
    public bool Failed => _failed;

    internal ProviderFailureReport? Outer { get; }

    internal void MarkFailed() => _failed = true;

    /// <summary>Closes the report: later failures go to the report around it, if any.</summary>
    public void Dispose() => ProviderCalls.Close(this);
}
