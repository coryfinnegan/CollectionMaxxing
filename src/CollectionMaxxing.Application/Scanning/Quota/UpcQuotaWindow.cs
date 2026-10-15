namespace CollectionMaxxing.Application.Scanning.Quota;

/// <summary>
/// UPCitemdb's allowance resets at midnight UTC (docs/02, "UPC quota strategy"). A code that found the allowance spent is
/// looked up again after the next reset.
/// </summary>
public static class UpcQuotaWindow
{
    /// <summary>The trace step <see cref="BarcodeResolver"/> records when the product lookup said its quota is spent.</summary>
    public const string ProductLookupStep = "product-lookup";

    public const string SpentOutcome = "quota";

    /// <summary>The first moment of the next UTC day: when the allowance is full again.</summary>
    public static DateTimeOffset NextResetAfter(DateTimeOffset now) =>
        new(DateTime.SpecifyKind(now.UtcDateTime.Date.AddDays(1), DateTimeKind.Utc));

    /// <summary>True when the answer is Pending because the daily quota is spent (not because a provider was down or slow).</summary>
    public static bool IsSpent(BarcodeResolution resolution) =>
        resolution.Verdict == ResolutionVerdict.Pending && IsSpent(resolution.Steps);

    public static bool IsSpent(IEnumerable<TraceStep> steps) =>
        steps.Any(s => s.Provider == ProductLookupStep && s.Outcome == SpentOutcome);

    /// <summary>The queue key for waiting on the reset at <paramref name="runAfter"/>: one job per code per day, however often it is asked.</summary>
    public static string DedupeKey(Guid candidateId, DateTimeOffset runAfter) =>
        $"{SubmitBarcodes.ResolveJobType}:{candidateId}:{runAfter.UtcDateTime:yyyyMMdd}";
}
