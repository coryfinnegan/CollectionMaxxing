namespace CollectionMaxxing.Domain.Catalog;

public enum AssetKind
{
    Cover,
    ScanPhoto,
    UserPhoto,
}

/// <summary>An image we store on the PC: a cover fetched once from an allow-listed host, or a photo the user chose to keep.</summary>
public sealed class Asset
{
    public Guid Id { get; init; }

    public AssetKind Kind { get; init; }

    public string BlobPath { get; init; } = string.Empty;

    public string ContentType { get; init; } = string.Empty;

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Lower-case hex SHA-256 of the file, to avoid storing the same image twice.</summary>
    public string Sha256 { get; init; } = string.Empty;

    public string? SourceUrl { get; init; }

    /// <summary>Null means shared; otherwise only that library can see it.</summary>
    public Guid? OwnerLibraryId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
