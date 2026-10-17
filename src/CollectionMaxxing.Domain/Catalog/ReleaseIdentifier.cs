namespace CollectionMaxxing.Domain.Catalog;

public enum IdentifierType
{
    Isbn13,
    Gtin13,
    Gtin8,
    Asin,
}

/// <summary>
/// A barcode or catalogue number of a release. One barcode can occasionally map to more than one release (reused UPCs),
/// so a value is unique per release, not on its own.
/// </summary>
public sealed class ReleaseIdentifier
{
    public Guid ReleaseId { get; init; }

    public IdentifierType Type { get; init; }

    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// The scan session whose commit taught the catalogue this link (the learning loop), or null for links from providers, imports and
    /// the person. Undoing that commit (SCAN-1e) deletes exactly the links it taught.
    /// </summary>
    public Guid? LearnedBySessionId { get; init; }
}
