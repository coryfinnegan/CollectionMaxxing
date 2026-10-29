using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Catalog.Matching;

/// <summary>
/// What was read from the item: a barcode's product name, a spine, a cover, a label. Every field is optional, and a term
/// the read did not supply is left out of the score (see <see cref="MatchScorer"/>).
/// </summary>
public sealed record ReadItem
{
    public string? Title { get; init; }

    /// <summary>The kind of work the reader believes it saw; used for the type term only when <see cref="Format"/> is missing.</summary>
    public WorkKind? Kind { get; init; }

    public ReleaseFormat? Format { get; init; }

    /// <summary>The platform as read ("N64", "Nintendo 64"). <see cref="PlatformNames"/> decides what counts as the same.</summary>
    public string? Platform { get; init; }

    /// <summary>
    /// The release year that was printed on the item (a copyright or release date). A year inside <see cref="Title"/>
    /// ("Madden NFL 2005") is part of the title, not a release year, and never stands in for this one.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>The author, director or developer that was read.</summary>
    public string? Creator { get; init; }

    /// <summary>An edition mark that was read: "Player's Choice", "Criterion".</summary>
    public string? Edition { get; init; }

    /// <summary>Barcodes and catalogue numbers that were read. One that equals a release's identifier is an exact match.</summary>
    public IReadOnlyList<Identifier> Identifiers { get; init; } = [];

    /// <summary>
    /// Feature-print distances the phone measured against candidate artwork, keyed by <see cref="CandidateRelease.Key"/>
    /// (docs/02 section 5). Null or empty when there was no artwork comparison, which drops the art term. Once any
    /// candidate of an evaluation has a usable distance, a candidate without one scores neutral on the art term instead.
    /// </summary>
    public IReadOnlyDictionary<string, double>? ArtDistances { get; init; }
}

/// <summary>A release a read is being compared with, in the shape the scorer needs (catalogue rows and provider results alike).</summary>
public sealed record CandidateRelease
{
    /// <summary>Identifies this candidate within one scoring run; artwork distances are reported against it.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public WorkKind Kind { get; init; } = WorkKind.Other;

    public ReleaseFormat? Format { get; init; }

    public string? Platform { get; init; }

    public int? Year { get; init; }

    public IReadOnlyList<string> Creators { get; init; } = [];

    public string? Edition { get; init; }

    public IReadOnlyList<Identifier> Identifiers { get; init; } = [];
}

public enum MatchOutcome
{
    /// <summary>
    /// Score at least 0.90 and a lead of at least 0.08 over the runner-up, from at least two compared signals or an exact
    /// barcode match, with no known conflict: included by default.
    /// </summary>
    Matched,

    /// <summary>Score at least 0.60 but not a clear winner: the person picks from the top few.</summary>
    Ambiguous,

    /// <summary>Nothing close enough: Fix it, enter manually or skip.</summary>
    NotFound,
}

public sealed record ScoredRelease(CandidateRelease Release, double Score)
{
    /// <summary>
    /// How many terms were actually compared: the profile gives them weight and both the read and the release had the
    /// datum. A term the release could not answer (no year, no platform on file) scores neutral and is not counted.
    /// </summary>
    public int ComparedSignals { get; init; }

    /// <summary>The read and the release share a barcode or catalogue number.</summary>
    public bool IsExactIdentifierMatch { get; init; }

    /// <summary>A known conflict (the wrong platform, or a different year in the title) capped the score.</summary>
    public bool HasKnownConflict { get; init; }

    /// <summary>
    /// The terms that made the score, in scoring order (title, type, platform, year, creator, edition, art): the score is
    /// their weighted mean, before any conflict cap. An exact identifier match lists only <c>identifier</c>.
    /// </summary>
    public IReadOnlyList<ScoreTerm> Terms { get; init; } = [];
}

/// <summary>
/// One term of a score: its profile weight and the value it scored, from 0 to 1. <paramref name="Compared"/> is false when
/// the release could not answer it and it stood at a neutral value (or a release with no title scored nothing on it).
/// </summary>
public sealed record ScoreTerm(string Name, double Weight, double Value, bool Compared);

/// <summary>The verdict and every candidate, best first. Take the first three for the "Pick one" list.</summary>
public sealed record MatchResult(MatchOutcome Outcome, IReadOnlyList<ScoredRelease> Ranked);
