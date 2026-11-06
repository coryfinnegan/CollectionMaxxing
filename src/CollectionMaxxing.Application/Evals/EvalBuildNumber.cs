using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The TestFlight build number a phone reports in its device record, so a run is credited to the build that read it. The rule is the one in
/// <c>app.config.ts</c>: one to three dot-separated integers. A value that breaks it is dropped, never a reason to refuse the run.
/// </summary>
public static partial class EvalBuildNumber
{
    public const int MaxLength = 20;

    [GeneratedRegex(@"\A[0-9]+(\.[0-9]+){0,2}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Shape();

    /// <summary>The trimmed build number when it is well formed; otherwise null.</summary>
    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 and <= MaxLength } && Shape().IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>The phone's build number, cleaned. A value that was sent but does not pass is dropped and logged at debug.</summary>
    public static string? FromInput(EvalDeviceInput device, ILogger logger)
    {
        var clean = Clean(device.BuildNumber);
        if (clean is null && !string.IsNullOrWhiteSpace(device.BuildNumber) && logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Eval run: dropped a malformed build number ({Length} characters).", device.BuildNumber.Length);
        }

        return clean;
    }
}
