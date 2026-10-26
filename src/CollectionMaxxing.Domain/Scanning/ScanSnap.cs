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
}

public enum FallbackReason
{
    Unavailable,
    Declined,
    Context,

    /// <summary>The model did not answer within its time budget (a slow read, not an outage).</summary>
    Timeout,
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

    /// <summary>True when the preferred engine wasn't used; <see cref="FallbackReason"/> says why.</summary>
    public bool FellBack { get; init; }

    public FallbackReason? FallbackReason { get; init; }

    public int ItemCount { get; init; }

    /// <summary>Null unless the photo was uploaded ("Use my photo").</summary>
    public Guid? AssetId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
