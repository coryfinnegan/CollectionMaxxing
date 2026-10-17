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

    /// <summary>
    /// The most a release can score when both titles carry a year and the years differ ("Madden NFL 2004" for a read of
    /// "Madden NFL 2005"). Keep it below <see cref="MatchedScore"/>, for the same reason as <see cref="PlatformMismatchCap"/>.
    /// </summary>
    public double TitleYearMismatchCap { get; init; } = 0.85;

    /// <summary>
    /// Checks that the settings cannot defeat the guards: each cap must be a number from 0 up to, but not including,
    /// <see cref="MatchedScore"/>, or a known-wrong release could be scored high enough to be auto-added.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is invalid; the message names every one.</exception>
    public void Validate()
    {
        var problems = new List<string>();
        if (!double.IsFinite(MatchedScore))
        {
            problems.Add($"{nameof(MatchedScore)} must be a finite number (it is {MatchedScore}).");
        }
        else
        {
            CheckCap(nameof(PlatformMismatchCap), PlatformMismatchCap);
            CheckCap(nameof(TitleYearMismatchCap), TitleYearMismatchCap);
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Invalid scoring thresholds. " + string.Join(' ', problems));
        }

        void CheckCap(string name, double cap)
        {
            if (!double.IsFinite(cap) || cap < 0 || cap >= MatchedScore)
            {
                problems.Add($"{name} must be at least 0 and below {nameof(MatchedScore)} ({MatchedScore}), but it is {cap}.");
            }
        }
    }
}

/// <summary>
/// How the artwork term is fed and when a scan asks for it (docs/02, "Artwork comparison with Apple feature prints"). The
/// scorer turns a distance <c>d</c> into a similarity of <c>1 - d^2 / 2</c> (the cosine of two unit vectors), clamped to 0..1.
/// </summary>
public sealed record ArtScoringOptions
{
    /// <summary>
    /// In score units (0 to 1, the same scale as <see cref="ScoringThresholds.MatchedLead"/>). A Matched candidate whose lead
    /// over the runner-up is below this is a close call: the phone is asked to compare artwork. An Ambiguous one always is.
    /// </summary>
    public double CloseCallMargin { get; init; } = 0.15;

    /// <summary>
    /// Unitless factor applied to every distance the phone reports, before the cosine mapping above. 1 treats the reported
    /// numbers as Euclidean distances between unit-length vectors. Calibrated at the M3 gate, when the real range of Apple's
    /// feature-print distances is measured on the device.
    /// </summary>
    public double DistanceScale { get; init; } = 1.0;

    /// <summary>
    /// The largest distance the server accepts, in the phone's own units (before <see cref="DistanceScale"/>). A bigger
    /// number is nonsense, and the request is refused rather than clamped.
    /// </summary>
    public double MaxDistance { get; init; } = 100.0;
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

    public ArtScoringOptions Art { get; init; } = new();

    public ScoringWeights BarcodeProduct { get; init; } = new(0.55, 0.15, 0.15, 0.10, 0.05, 0, 0);

    public ScoringWeights ShelfSpine { get; init; } = new(0.50, 0.10, 0.15, 0.05, 0.20, 0, 0);

    public ScoringWeights Cover { get; init; } = new(0.35, 0.10, 0.15, 0.05, 0.05, 0.10, 0.20);

    public ScoringWeights LooseCartridge { get; init; } = new(0.40, 0.05, 0.30, 0.05, 0, 0.05, 0.15);

    public ScoringWeights BareLabel { get; init; } = new(0.55, 0.15, 0.15, 0.10, 0, 0, 0.05);

    /// <summary>
    /// Fails fast on settings that would let a wrong release through (see <see cref="ScoringThresholds.Validate"/>).
    /// Call it when the options are bound at startup, so that a bad configuration stops the app from starting;
    /// <see cref="Profile"/> checks again on every use.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is invalid.</exception>
    public void Validate() => Thresholds.Validate();

    /// <summary>The profile for one way of reading an item, carrying these options' thresholds.</summary>
    /// <exception cref="InvalidOperationException">The options are invalid (see <see cref="Validate"/>).</exception>
    public ScoringProfile Profile(ScoringProfileKind kind)
    {
        Validate();
        return kind switch
        {
            ScoringProfileKind.BarcodeProduct => new ScoringProfile(nameof(BarcodeProduct), BarcodeProduct, Thresholds),
            ScoringProfileKind.ShelfSpine => new ScoringProfile(nameof(ShelfSpine), ShelfSpine, Thresholds),
            ScoringProfileKind.Cover => new ScoringProfile(nameof(Cover), Cover, Thresholds),
            ScoringProfileKind.LooseCartridge => new ScoringProfile(nameof(LooseCartridge), LooseCartridge, Thresholds),
            ScoringProfileKind.BareLabel => new ScoringProfile(nameof(BareLabel), BareLabel, Thresholds),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown scoring profile."),
        };
    }
}
