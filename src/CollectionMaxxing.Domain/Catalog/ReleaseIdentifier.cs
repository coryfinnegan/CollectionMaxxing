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
/// so a value is unique per release, not on its own. A link is either shared or learned: a link from a provider, the
/// catalogue or an import has no <see cref="LibraryId"/> and is the same for everyone; a link a person taught by picking
/// a release by hand belongs to that person's library and is used only there.
/// </summary>
public sealed class ReleaseIdentifier
{
    /// <summary>
    /// The row's own key. The same code may be learned for the same release by several libraries, and by none (a shared link),
    /// so (type, value, release) is only unique per library; two partial unique indexes keep it so.
    /// </summary>
    public Guid Id { get; init; }

    public Guid ReleaseId { get; init; }

    public IdentifierType Type { get; init; }

    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// The scan session whose commit taught the catalogue this link (the learning loop), or null for links from providers, imports and
    /// the person. Undoing that commit (SCAN-1e) deletes exactly the links it taught.
    /// </summary>
    public Guid? LearnedBySessionId { get; init; }

    /// <summary>
    /// The library that taught this link, or null for a shared one (a provider, the catalogue or an import). A learned link
    /// answers a scan in this library only, so one library's hand pick, right or wrong, never becomes another's answer.
    /// It goes with the library.
    /// </summary>
    public Guid? LibraryId { get; init; }
}
