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

    /// <summary>A year that was printed on the item. A trailing year in <see cref="Title"/> is used when this is null.</summary>
    public int? Year { get; init; }

    /// <summary>The author, director or developer that was read.</summary>
    public string? Creator { get; init; }

    /// <summary>An edition mark that was read: "Player's Choice", "Criterion".</summary>
    public string? Edition { get; init; }

    /// <summary>Barcodes and catalogue numbers that were read. One that equals a release's identifier is an exact match.</summary>
    public IReadOnlyList<Identifier> Identifiers { get; init; } = [];

    /// <summary>
    /// Feature-print distances the phone measured against candidate artwork, keyed by <see cref="CandidateRelease.Key"/>
    /// (docs/02 section 5). Null or empty when there was no artwork comparison, which drops the art term.
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
    /// <summary>Score at least 0.90 and a lead of at least 0.08 over the runner-up: included by default.</summary>
    Matched,

    /// <summary>Score at least 0.60 but not a clear winner: the person picks from the top few.</summary>
    Ambiguous,

    /// <summary>Nothing close enough: Fix it, enter manually or skip.</summary>
    NotFound,
}

public sealed record ScoredRelease(CandidateRelease Release, double Score);

/// <summary>The verdict and every candidate, best first. Take the first three for the "Pick one" list.</summary>
public sealed record MatchResult(MatchOutcome Outcome, IReadOnlyList<ScoredRelease> Ranked);
