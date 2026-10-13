namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>The weight of each term of the score (docs/02 section 6). Weights are relative: only their proportions matter.</summary>
public sealed record ScoringWeights(
    double Title,
    double Type,
    double Platform,
    double Year,
    double Creator,
    double Edition,
    double Art);

/// <summary>The outcome rules and the platform-mismatch cap (docs/02 section 6). Defaults are the doc's values.</summary>
public sealed record ScoringThresholds
{
    /// <summary>A score at or above this, with <see cref="MatchedLead"/>, is Matched.</summary>
    public double MatchedScore { get; init; } = 0.90;

    /// <summary>How far ahead of the runner-up the best score must be to be Matched.</summary>
    public double MatchedLead { get; init; } = 0.08;

    /// <summary>A score at or above this is at least Ambiguous.</summary>
    public double AmbiguousScore { get; init; } = 0.60;

    /// <summary>
    /// The most a release can score when its platform is known to differ from the one read. Keep it below
    /// <see cref="MatchedScore"/>: a right game on the wrong console must never be offered as a sure thing.
    /// </summary>
    public double PlatformMismatchCap { get; init; } = 0.85;
}

/// <summary>A named set of weights and the thresholds to apply, for one way of reading an item.</summary>
public sealed record ScoringProfile(string Name, ScoringWeights Weights, ScoringThresholds Thresholds);

public enum ScoringProfileKind
{
    /// <summary>A product name from a barcode lookup.</summary>
    BarcodeProduct,

    /// <summary>A book spine on a shelf.</summary>
    ShelfSpine,

    /// <summary>A cover or box.</summary>
    Cover,

    /// <summary>A loose cartridge: the platform matters most.</summary>
    LooseCartridge,

    /// <summary>A bare label on a tape or disc.</summary>
    BareLabel,
}

/// <summary>
/// The scoring configuration. The defaults are the profile table and outcome rules of docs/02 section 6; bind this record
/// from configuration to tune them against the eval set.
/// </summary>
public sealed record ScoringOptions
{
    public ScoringThresholds Thresholds { get; init; } = new();

    public ScoringWeights BarcodeProduct { get; init; } = new(0.55, 0.15, 0.15, 0.10, 0.05, 0, 0);

    public ScoringWeights ShelfSpine { get; init; } = new(0.50, 0.10, 0.15, 0.05, 0.20, 0, 0);

    public ScoringWeights Cover { get; init; } = new(0.35, 0.10, 0.15, 0.05, 0.05, 0.10, 0.20);

    public ScoringWeights LooseCartridge { get; init; } = new(0.40, 0.05, 0.30, 0.05, 0, 0.05, 0.15);

    public ScoringWeights BareLabel { get; init; } = new(0.55, 0.15, 0.15, 0.10, 0, 0, 0.05);

    /// <summary>The profile for one way of reading an item, carrying these options' thresholds.</summary>
    public ScoringProfile Profile(ScoringProfileKind kind) => kind switch
    {
        ScoringProfileKind.BarcodeProduct => new ScoringProfile(nameof(BarcodeProduct), BarcodeProduct, Thresholds),
        ScoringProfileKind.ShelfSpine => new ScoringProfile(nameof(ShelfSpine), ShelfSpine, Thresholds),
        ScoringProfileKind.Cover => new ScoringProfile(nameof(Cover), Cover, Thresholds),
        ScoringProfileKind.LooseCartridge => new ScoringProfile(nameof(LooseCartridge), LooseCartridge, Thresholds),
        ScoringProfileKind.BareLabel => new ScoringProfile(nameof(BareLabel), BareLabel, Thresholds),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown scoring profile."),
    };
}
