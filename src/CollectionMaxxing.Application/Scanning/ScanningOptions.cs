namespace CollectionMaxxing.Application.Scanning;

/// <summary>Tuning for the scanner. Registered as a singleton; tests shorten the times.</summary>
public sealed class ScanningOptions
{
    /// <summary>The most codes one <c>POST …/barcodes</c> may carry (docs/04).</summary>
    public const int MaxCodesPerBatch = 50;

    /// <summary>The most copies one line may stand for.</summary>
    public const int MaxCopies = 99;

    /// <summary>How long a request waits for codes to resolve before it answers with the rest still Pending (docs/04: 2 s).</summary>
    public TimeSpan ResolveBudget { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the background job gives one code, which no request is waiting for.</summary>
    public TimeSpan JobResolveBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many codes of one batch are looked up at the same time.</summary>
    public int Parallelism { get; set; } = 4;
}
