using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Exports;

/// <summary>A zip that is not one of ours: a manifest is missing. <see cref="Entry"/> names it.</summary>
public sealed class ExportFormatException(string entry)
    : IOException($"That export is missing {entry}.")
{
    public string Entry { get; } = entry;
}

/// <summary>
/// The import side of an export zip: reads what <see cref="BuildExport"/> wrote back into typed rows. The round-trip
/// test proves an export carries the whole collection (items, covers, metadata); a future restore endpoint will parse
/// with this and write the rows back. The CSV is the human copy and is not read: the JSON is the preservation format.
/// A photo whose file is missing reads null bytes, the way the exporter lists a lost file with no <c>file</c>.
/// </summary>
public static class ReadExport
{
    public sealed record SnapshotItem(
        Guid Id,
        Guid LibraryId,
        string Library,
        Guid ReleaseId,
        Guid WorkId,
        string Title,
        string? CustomTitle,
        WorkKind Kind,
        ReleaseFormat Format,
        string? Platform,
        int? Year,
        ItemStatus Status,
        ItemCondition? Condition,
        ItemCompleteness? Completeness,
        Guid? LocationId,
        string? Location,
        IReadOnlyList<Guid> TagIds,
        IReadOnlyList<string> Tags,
        string? Notes,
        DateOnly? AcquiredOn,
        string? LentTo,
        DateOnly? LentOn,
        IReadOnlyList<SnapshotIdentifier> Identifiers,
        string? AddedBy,
        DateTimeOffset AddedAt,
        DateTimeOffset UpdatedAt);

    public sealed record SnapshotIdentifier(string Type, string Value);

    public sealed record SnapshotLocation(Guid Id, Guid LibraryId, Guid? ParentId, string Name, string Path, int SortOrder);

    public sealed record SnapshotTag(Guid Id, Guid LibraryId, string Name);

    public sealed record SnapshotPhoto(
        Guid AssetId,
        Guid LibraryId,
        string ContentType,
        int? Width,
        int? Height,
        DateTimeOffset CreatedAt,
        IReadOnlyList<Guid> ItemIds,
        string? File,
        byte[]? Data = null);

    public sealed record SnapshotFeaturePrint(Guid ItemId, Guid LibraryId, Guid ReleaseId, int Revision, int Bytes, DateTimeOffset CreatedAt);

    public sealed record Snapshot(
        IReadOnlyList<SnapshotItem> Items,
        IReadOnlyList<SnapshotLocation> Locations,
        IReadOnlyList<SnapshotTag> Tags,
        IReadOnlyList<SnapshotPhoto> Photos,
        IReadOnlyList<SnapshotFeaturePrint> FeaturePrints);

    // The writer's options, so enums parse back to the values they were written from.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Reads the whole zip from <paramref name="zip"/> (which stays open).</summary>
    /// <exception cref="ExportFormatException">A manifest entry is missing.</exception>
    /// <exception cref="JsonException">A manifest is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a zip at all.</exception>
    public static Snapshot Read(Stream zip)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var items = ReadJson<SnapshotItem>(archive, "items.json");
        var locations = ReadJson<SnapshotLocation>(archive, "locations.json");
        var tags = ReadJson<SnapshotTag>(archive, "tags.json");
        var prints = ReadJson<SnapshotFeaturePrint>(archive, "feature-prints.json");
        var photos = ReadJson<SnapshotPhoto>(archive, "photos.json")
            .Select(p => p with { Data = p.File is null ? null : ReadBytes(archive, p.File) })
            .ToList();
        return new Snapshot(items, locations, tags, photos, prints);
    }

    private static List<T> ReadJson<T>(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new ExportFormatException(name);
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<List<T>>(stream, Json) ?? [];
    }

    private static byte[]? ReadBytes(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
