using System.Text.Json.Serialization;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Evals;

/// <summary>How a photo went on the phone.</summary>
public enum EvalOutcome
{
    /// <summary>The engine answered (possibly with no items).</summary>
    Ok,

    /// <summary>The engine declined the picture (a guardrail refusal).</summary>
    Refused,

    /// <summary>The engine failed or was unavailable.</summary>
    Error,

    /// <summary>The model did not answer within its time budget. It counts in the latency percentiles (the slowest reads) and has its own rate.</summary>
    Timeout,
}

/// <summary>The phone's thermal state at the end of the photo (iOS <c>ProcessInfo.ThermalState</c>).</summary>
public enum ThermalState
{
    Nominal,
    Fair,
    Serious,
    Critical,
}

/// <summary>Which step settled an item (docs/09: model, Vision text, artwork comparison, Fix it).</summary>
public enum ResolvedStep
{
    Model,
    VisionText,
    Artwork,
    FixIt,
}

public sealed record EvalDevice(string Model, string OsVersion, string AppVersion);

// ---- What the phone uploads (the API maps its request to these) -----------------------------------------------------------

public sealed record EvalDeviceInput(string? Model, string? OsVersion, string? AppVersion);

/// <summary>One item as the phone read it (the same fields as an <c>/identify</c> item) and the step that settled it.</summary>
public sealed record EvalReadItemInput(
    Scanning.Identify.IdentifyItemInput Read,
    ResolvedStep? ResolvedBy,
    ScanTier? Tier = null,
    bool Provisional = false,
    bool? IsGuess = null);

/// <summary>
/// What a duplicate re-scan of the photo's shelf showed (docs/16 §17): how many owned items it hit, how many of those
/// were answered on the card, and how many new items appeared without an explicit Add-copy.
/// </summary>
public sealed record DuplicateRescanInput(int OwnedHits, int AnsweredOnCard, int UnsolicitedAdds);

public sealed record EvalPhotoInput(
    string? PhotoId,
    ScanEngine? Engine,
    EvalOutcome? Outcome,
    FallbackReason? FellBackFrom,
    IReadOnlyList<EvalReadItemInput>? Items,
    int? UnreadableCount,
    long? LatencyMs,
    long? FirstItemMs,
    ThermalState? Thermal,
    int? CapturedCount = null,
    DateTimeOffset? CapturedAt = null,
    DuplicateRescanInput? DuplicateRescan = null);

/// <param name="Partial">The phone cancelled the run part way. It is kept and shown, but never counts as a pass or a baseline.</param>
/// <param name="BaselineNote">Why this run's thresholds may differ from the baseline's (docs/16 §17). Null most runs.</param>
public sealed record EvalRunInput(
    EvalDeviceInput? Device,
    string? ConfigVersion,
    IReadOnlyList<EvalPhotoInput>? Results,
    bool Partial = false,
    string? BaselineNote = null);

// ---- What the server keeps of a run, after grounding ----------------------------------------------------------------------

/// <summary>A release the server found for a read: the title and the fields that are compared with the truth.</summary>
public sealed record GroundedCandidate(string Title, string? Kind, string? Format, string? Platform, int? Year, double Score);

/// <summary>
/// One read item after grounding: what the phone read, the verdict the live scorer gave and the top three releases (best first).
/// Kept on the PC only: it has titles. <c>Tier</c> is the commit tier the pipeline assigned (<c>T0</c>, <c>T1</c> or <c>T2</c>),
/// null when the run predates tiers; <c>Provisional</c> marks a kept guess, which must carry <c>IsGuess</c> (docs/16 §17).
/// </summary>
public sealed record GroundedItem(
    string? ReadTitle,
    string? ReadCompleteness,
    string ResolvedBy,
    string Verdict,
    IReadOnlyList<GroundedCandidate> Candidates,
    string? Tier = null,
    bool Provisional = false,
    bool? IsGuess = null,
    string? SegmentId = null)
{
    /// <summary>The best release, or null when there is none.</summary>
    [JsonIgnore]
    public GroundedCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>
/// What a duplicate re-scan of the photo's shelf showed: how many owned items it hit, how many of those were answered
/// on the card, and how many new items appeared without an explicit Add-copy. Null when the photo was not a re-scan.
/// </summary>
public sealed record DuplicateRescan(int OwnedHits, int AnsweredOnCard, int UnsolicitedAdds);

public sealed record GroundedPhoto(
    string PhotoId,
    string Engine,
    string? FellBackFrom,
    string Outcome,
    IReadOnlyList<GroundedItem> Items,
    int UnreadableCount,
    long LatencyMs,
    long? FirstItemMs,
    string ThermalState,
    int CapturedCount = 0,
    DateTimeOffset? CapturedAt = null,
    DuplicateRescan? DuplicateRescan = null);

/// <summary>A whole run after grounding. Kept on the PC (it has titles): re-scoring it needs no phone and no provider.</summary>
public sealed record GroundedRun(
    string RunId,
    DateTimeOffset At,
    EvalDevice Device,
    string ConfigVersion,
    IReadOnlyList<GroundedPhoto> Photos,
    bool Partial = false,
    string? BaselineNote = null);

public static class ResolvedByText
{
    public const string Model = "model";
    public const string VisionText = "vision_text";
    public const string Artwork = "artwork";
    public const string FixIt = "fix_it";

    /// <summary>What settled an item when the phone did not say: the engine that read it.</summary>
    public static string For(ScanEngine engine, ResolvedStep? said) => said switch
    {
        ResolvedStep.Model => Model,
        ResolvedStep.VisionText => VisionText,
        ResolvedStep.Artwork => Artwork,
        ResolvedStep.FixIt => FixIt,
        _ => engine == ScanEngine.AppleModel ? Model : VisionText,
    };
}
