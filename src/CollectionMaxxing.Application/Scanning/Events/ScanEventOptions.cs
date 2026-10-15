namespace CollectionMaxxing.Application.Scanning.Events;

/// <summary>Tuning for the scan event stream. Registered as a singleton; tests shorten the times.</summary>
public sealed class ScanEventOptions
{
    /// <summary>How often a connected stream looks for changes. Sub-second, so the scanner feels live.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A comment line is sent this often when nothing else is, so proxies and phones keep the connection open (docs/04: 15 s).</summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(15);
}
