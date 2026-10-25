using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Catalog;

/// <summary>Puts known catalogue rows in the database for the catalogue tests.</summary>
public static class CatalogSeed
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A work with one release (normalised title, like the real importers write it). A release owned by a library is private to it.</summary>
    public static async Task<(Guid WorkId, Guid ReleaseId)> AddAsync(
        PostgresFixture db,
        string title,
        WorkKind kind = WorkKind.Film,
        int? year = 1999,
        Guid? platformId = null,
        Guid? ownerLibraryId = null,
        string? barcode = null,
        IdentifierType barcodeType = IdentifierType.Gtin13,
        Guid? coverAssetId = null,
        ReleaseFormat format = ReleaseFormat.Dvd)
    {
        var work = new Work
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            NormalizedTitle = TitleNormalizer.Normalize(title),
            OriginalYear = year,
            CoverAssetId = coverAssetId,
            CreatedAt = Start,
        };
        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = work.Id,
            Format = format,
            PlatformId = platformId,
            Source = ownerLibraryId is null ? ReleaseSource.Provider : ReleaseSource.User,
            OwnerLibraryId = ownerLibraryId,
            CoverAssetId = coverAssetId,
            CreatedAt = Start,
        };

        await using var context = db.CreateContext();
        context.Works.Add(work);
        context.Releases.Add(release);
        if (barcode is not null)
        {
            context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = release.Id, Type = barcodeType, Value = barcode });
        }

        await context.SaveChangesAsync();
        return (work.Id, release.Id);
    }

    public static async Task<Guid> AddPlatformAsync(PostgresFixture db, string name, string shortName)
    {
        var platform = new Platform { Id = Guid.CreateVersion7(), Name = name, ShortName = shortName };
        await using var context = db.CreateContext();
        context.Platforms.Add(platform);
        await context.SaveChangesAsync();
        return platform.Id;
    }

    public static async Task<Guid> AddAssetAsync(
        PostgresFixture db, string blobPath, string contentType = "image/png", Guid? ownerLibraryId = null, string sha256 = "ab12cd34")
    {
        var asset = new Asset
        {
            Id = Guid.CreateVersion7(),
            Kind = AssetKind.Cover,
            BlobPath = blobPath,
            ContentType = contentType,
            Sha256 = sha256,
            OwnerLibraryId = ownerLibraryId,
            CreatedAt = Start,
        };
        await using var context = db.CreateContext();
        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        return asset.Id;
    }
}
