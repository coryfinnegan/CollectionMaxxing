namespace CollectionMaxxing.Api.Security;

/// <summary>Settings under <c>Security:</c>. The defaults are the numbers in docs/08; every one can be changed in configuration.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public LimitOptions Limits { get; set; } = new();

    /// <summary>How long one request may run before it is cancelled and answered with a 504. Event streams and nothing else are exempt.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>The largest request body Kestrel accepts (docs/08, "Upload pipeline"); endpoints with a smaller cap keep theirs.</summary>
    public long MaxRequestBodyBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Send <c>Strict-Transport-Security</c> (browsers ignore it over plain HTTP). Default: on outside Development.</summary>
    public bool? Hsts { get; set; }

    /// <summary>One bucket size and the time it covers, for one group of endpoints.</summary>
    public sealed record GroupLimit
    {
        public GroupLimit()
        {
        }

        public GroupLimit(int permits, int windowSeconds)
        {
            Permits = permits;
            WindowSeconds = windowSeconds;
        }

        public int Permits { get; set; }

        public int WindowSeconds { get; set; }
    }

    /// <summary>The "Rate limits" table of docs/08 (the 10-a-minute code and passkey limiter is <c>Auth:RateLimit</c>).</summary>
    public sealed class LimitOptions
    {
        /// <summary>Everything else: 600 a minute per user (a token bucket, so short bursts are fine).</summary>
        public GroupLimit Default { get; set; } = new(600, 60);

        /// <summary>Catalogue search and code lookup: 60 a minute per user.</summary>
        public GroupLimit Search { get; set; } = new(60, 60);

        /// <summary>Barcode batches: 120 a minute per user.</summary>
        public GroupLimit Barcodes { get; set; } = new(120, 60);

        /// <summary>Picture identification: 1,000 <em>items</em> an hour per user (one call with 40 items costs 40).</summary>
        public GroupLimit Identify { get; set; } = new(1000, 3600);

        /// <summary>Starting an export: once every 10 minutes per user.</summary>
        public GroupLimit Export { get; set; } = new(1, 600);

        /// <summary>The code and passkey endpoints, on top of the per-minute limit: 30 an hour per address.</summary>
        public GroupLimit AuthHourly { get; set; } = new(30, 3600);

        public IEnumerable<(string Name, GroupLimit Limit)> All =>
        [
            (nameof(Default), Default), (nameof(Search), Search), (nameof(Barcodes), Barcodes),
            (nameof(Identify), Identify), (nameof(Export), Export), (nameof(AuthHourly), AuthHourly),
        ];
    }
}
