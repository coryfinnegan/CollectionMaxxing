using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Catalog;

// Response shapes of the catalogue endpoints (docs/04). Fields are the contract: change them deliberately.

/// <summary>A creative work: the novel, the film, the game.</summary>
public sealed record WorkDto(
    Guid Id,
    WorkKind Kind,
    string Title,
    int? OriginalYear,
    IReadOnlyList<string> Creators,
    IReadOnlyList<string> Genres,
    string? Synopsis,
    string? CoverUrl);

/// <summary>A barcode or catalogue number of a release.</summary>
public sealed record ReleaseIdentifierDto(IdentifierType Type, string Value);

/// <summary>A specific physical product: the 1993 VHS, the N64 cartridge.</summary>
public sealed record ReleaseDto(
    Guid Id,
    Guid WorkId,
    string Title,
    ReleaseFormat Format,
    PlatformDto? Platform,
    ReleaseRegion? Region,
    string? Edition,
    string? Publisher,
    DateOnly? ReleaseDate,
    int? Year,
    IReadOnlyList<string> Creators,
    int? PageCount,
    int? RuntimeMinutes,
    string? CoverUrl,
    ReleaseSource Source,
    IReadOnlyList<ReleaseIdentifierDto> Identifiers);

/// <summary>A games platform (reference data).</summary>
public sealed record PlatformDto(Guid Id, string Name, string ShortName, string? Manufacturer);

/// <summary>One work found by a catalogue search, with its releases, from the local catalogue or a provider.</summary>
public sealed record CatalogSearchResultDto(WorkDto Work, IReadOnlyList<ReleaseDto> Releases, double Score, string Source);

/// <summary>The releases a barcode or catalogue number resolves to. Usually one; a reused UPC can be more.</summary>
public sealed record CatalogLookupDto(string Code, IReadOnlyList<ReleaseDto> Releases);

/// <summary>Reference artwork of a release (box, cover, cartridge label) to show beside the user's photo.</summary>
public sealed record ArtworkDto(Guid AssetId, AssetKind Kind, string Url, int? Width, int? Height);

/// <summary>The Apple feature print of an item's artwork, as backed up by the phone. Opaque to the server.</summary>
public sealed record FeaturePrintDto(Guid ItemId, Guid ReleaseId, int Revision, string Data, DateTimeOffset CreatedAt);
