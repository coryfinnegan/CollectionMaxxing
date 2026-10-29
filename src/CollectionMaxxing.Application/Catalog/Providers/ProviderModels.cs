using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Application.Catalog.Providers;

/// <summary>What a provider knows about one release, before it is matched or stored.</summary>
public sealed record ProviderRelease
{
    /// <summary>The provider's <see cref="ICatalogProvider.Name"/>.</summary>
    public required string Provider { get; init; }

    /// <summary>The provider's own id for the release; unique within <see cref="Provider"/>.</summary>
    public required string ExternalId { get; init; }

    /// <summary>
    /// The provider's id for the work (the novel, the film, the game) when it names one apart from the release:
    /// Open Library work keys, TMDB ids, IGDB game ids. Null when the provider names only releases (Google Books volumes).
    /// </summary>
    public string? WorkKey { get; init; }

    public required string Title { get; init; }

    public WorkKind Kind { get; init; } = WorkKind.Other;

    public ReleaseFormat? Format { get; init; }

    /// <summary>The platform as the provider names it ("Nintendo 64").</summary>
    public string? Platform { get; init; }

    public int? Year { get; init; }

    public IReadOnlyList<string> Creators { get; init; } = [];

    public string? Edition { get; init; }

    public string? Publisher { get; init; }

    public string? CoverUrl { get; init; }

    public string? Synopsis { get; init; }

    public IReadOnlyList<Identifier> Identifiers { get; init; } = [];

    /// <summary>
    /// This release in the format the item was read as, when the provider named none and the format fits the work: TMDB
    /// answers a film and IGDB a game with no format at all, so a DVD cover would otherwise be stored as format Unknown.
    /// A format the provider did name is kept.
    /// </summary>
    public ProviderRelease WithReadFormat(ReleaseFormat? read) =>
        !ReleaseFormats.IsKnown(Format) && ReleaseFormats.IsKnown(read) && ReleaseFormats.Fits(read!.Value, Kind)
            ? this with { Format = read }
            : this;

    /// <summary>This release as a candidate for the scorer, keyed by <c>provider:externalId</c>.</summary>
    public CandidateRelease ToCandidate() => new()
    {
        Key = $"{Provider}:{ExternalId}",
        Title = Title,
        Kind = Kind,
        Format = Format,
        Platform = Platform,
        Year = Year,
        Creators = Creators,
        Edition = Edition,
        Identifiers = Identifiers,
    };
}

/// <summary>A catalogue search: a title, narrowed by whatever else is known.</summary>
public sealed record CatalogQuery
{
    public required string Title { get; init; }

    public WorkKind? Kind { get; init; }

    public ReleaseFormat? Format { get; init; }

    public string? Platform { get; init; }

    public int? Year { get; init; }

    public string? Creator { get; init; }

    /// <summary>The most results wanted.</summary>
    public int Limit { get; init; } = 10;
}
