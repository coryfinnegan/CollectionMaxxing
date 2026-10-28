using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Scanning;

public enum SnapTrigger
{
    Auto,
    Shutter,
    Import,
}

public enum ScanEngine
{
    AppleModel,
    VisionPipeline,

    /// <summary>The on-device model extracting typed items from Vision OCR text (no picture reaches the model; #508).</summary>
    FmText,
}

public enum FallbackReason
{
    Unavailable,
    Declined,
    Context,

    /// <summary>The model did not answer within its time budget (a slow read, not an outage).</summary>
    Timeout,

    /// <summary>The classifier asked for a reshoot: move closer — spines too small (design §8(a)).</summary>
    MoveCloser,

    /// <summary>The classifier asked for a reshoot: split the segment — shoot left/right halves (design §8(a)).</summary>
    SplitSegment,

    /// <summary>The classifier asked for a reshoot: hold steady — add light (design §8(a)).</summary>
    HoldSteady,

    /// <summary>The classifier asked for a reshoot: tilt to kill the glare (design §8(a)).</summary>
    KillGlare,
}

/// <summary>Metadata about one photo the phone analysed. The photo itself stays on the phone.</summary>
public sealed class ScanSnap : ILibraryScoped
{
    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    public Guid SessionId { get; init; }

    public SnapTrigger Trigger { get; init; }

    /// <summary>What the phone's framing said this shot was. A phone that does not send one leaves it unknown.</summary>
    public ShotKind ShotKind { get; init; } = ShotKind.Unknown;

    public ScanEngine Engine { get; init; }

    /// <summary>True when a fallback was recorded: the engine fell back, or the classifier asked for a reshoot; <see cref="FallbackReason"/> says which.</summary>
    public bool FellBack { get; init; }

    public FallbackReason? FallbackReason { get; init; }

    public int ItemCount { get; init; }

    /// <summary>Null unless the photo was uploaded ("Use my photo").</summary>
    public Guid? AssetId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
