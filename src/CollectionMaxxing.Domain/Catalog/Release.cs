namespace CollectionMaxxing.Domain.Catalog;

public enum ReleaseFormat
{
    Hardcover,
    Paperback,
    MassMarket,
    Vhs,
    Betamax,
    Laserdisc,
    Dvd,
    Bluray,
    UhdBluray,
    GameCartridge,
    GameDisc,
    GameCard,
    Other,
}

public enum ReleaseRegion
{
    NtscU,
    Pal,
    NtscJ,
    RegionFree,
}

public enum ReleaseSource
{
    Provider,
    User,
    Ai,
}

/// <summary>
/// A specific physical product you can hold: the 1993 VHS, the N64 cartridge. Global unless <see cref="OwnerLibraryId"/>
/// is set, in which case only that library sees it (a user-created release, until it is verified).
/// </summary>
public sealed class Release
{
    public Guid Id { get; init; }

    public Guid WorkId { get; init; }

    public ReleaseFormat Format { get; set; }

    public Guid? PlatformId { get; set; }

    public ReleaseRegion? Region { get; set; }

    public string? Edition { get; set; }

    public string? Publisher { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    public int? PageCount { get; set; }

    public int? RuntimeMinutes { get; set; }

    public Guid? CoverAssetId { get; set; }

    public ReleaseSource Source { get; init; }

    /// <summary>Null means global.</summary>
    public Guid? OwnerLibraryId { get; init; }

    /// <summary>What the providers said, kept so a match can be explained or refreshed.</summary>
    public string? ProviderPayloadsJson { get; set; }

    public DateTimeOffset? RefreshedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
