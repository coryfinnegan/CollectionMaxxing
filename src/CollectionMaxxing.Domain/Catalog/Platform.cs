namespace CollectionMaxxing.Domain.Catalog;

/// <summary>Reference data: a games platform (NES, N64, PS5, …), seeded from IGDB.</summary>
public sealed class Platform
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public string ShortName { get; set; } = string.Empty;

    public string? Manufacturer { get; set; }

    public int? IgdbId { get; set; }
}
