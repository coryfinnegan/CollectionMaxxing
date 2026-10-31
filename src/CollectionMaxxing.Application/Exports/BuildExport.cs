using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Exports;

/// <summary>
/// The background job that writes one person's export zip: <c>items.csv</c>, <c>items.json</c>, <c>locations.json</c> and <c>tags.json</c>
/// for every library they belong to right now. Items are read in batches (never the whole library at once). Soft-deleted items are left out.
/// <c>photos.json</c> lists the photos those libraries keep and the zip holds the files as <c>photos/&lt;assetId&gt;.jpg</c>;
/// <c>feature-prints.json</c> lists the feature-print backups (manifest only: ids, sizes and dates, never the bytes). The zip holds only the library's own data and display names: no emails, tokens, hashes or audit rows.
/// </summary>
public static class BuildExport
{
    public const string JobType = "exports.build";

    /// <summary>The job's payload. <paramref name="ExportId"/> is the handle the person polls; it names the file.</summary>
    public sealed record Payload(Guid ExportId, Guid UserId);

    private static readonly string[] CsvHeader =
    [
        "library", "title", "kind", "format", "platform", "year", "status", "condition", "location", "tags", "notes",
        "acquired_on", "identifiers", "added_by", "added_at",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = false,
    };

    private static readonly UTF8Encoding Utf8WithBom = new(true);

    private sealed class Row
    {
        public required Guid Id { get; init; }

        public required Guid ReleaseId { get; init; }

        public required Guid WorkId { get; init; }

        public required string WorkTitle { get; init; }

        public string? CustomTitle { get; init; }

        public required WorkKind Kind { get; init; }

        public required ReleaseFormat Format { get; init; }

        public string? Platform { get; init; }

        public DateOnly? ReleaseDate { get; init; }

        public int? OriginalYear { get; init; }

        public required ItemStatus Status { get; init; }

        public ItemCondition? Condition { get; init; }

        public ItemCompleteness? Completeness { get; init; }

        public Guid? LocationId { get; init; }

        public string? Notes { get; init; }

        public DateOnly? AcquiredOn { get; init; }

        public string? LentTo { get; init; }

        public DateOnly? LentOn { get; init; }

        public required Guid CreatedBy { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed record ExportIdentifier(string Type, string Value);

    private sealed record ExportItem(
        Guid Id,
        Guid LibraryId,
        string Library,
        Guid ReleaseId,
        Guid WorkId,
        string Title,
        string? CustomTitle,
        string Kind,
        string Format,
        string? Platform,
        int? Year,
        string Status,
        string? Condition,
        string? Completeness,
        Guid? LocationId,
        string? Location,
        IReadOnlyList<Guid> TagIds,
        IReadOnlyList<string> Tags,
        string? Notes,
        DateOnly? AcquiredOn,
        string? LentTo,
        DateOnly? LentOn,
        IReadOnlyList<ExportIdentifier> Identifiers,
        string? AddedBy,
        DateTimeOffset AddedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ExportLocation(Guid Id, Guid LibraryId, Guid? ParentId, string Name, string Path, int SortOrder);

    private sealed record ExportTag(Guid Id, Guid LibraryId, string Name);

    /// <summary>
    /// A photo the library keeps ("Use my photo" covers). The manifest lists it and <c>File</c> names its entry in the zip
    /// (<c>photos/&lt;assetId&gt;.jpg</c>); it is null when the file is no longer on the PC.
    /// </summary>
    private sealed record ExportPhoto(
        Guid AssetId, Guid LibraryId, string ContentType, int? Width, int? Height, DateTimeOffset CreatedAt, IReadOnlyList<Guid> ItemIds, string? File = null);

    private sealed record PhotoToExport(ExportPhoto Photo, string BlobPath);

    /// <summary>A backed-up feature print: which item and release it is for and how big it is. The bytes are the phone's and mean nothing elsewhere.</summary>
    private sealed record ExportFeaturePrint(Guid ItemId, Guid LibraryId, Guid ReleaseId, int Revision, int Bytes, DateTimeOffset CreatedAt);

    private sealed record LibraryData(
        Guid Id, string Name, IReadOnlyDictionary<Guid, string> LocationPaths, IReadOnlyDictionary<Guid, string> TagNames);

    public sealed class Handler(
        CurrentUser currentUser,
        IMembershipReader memberships,
        IAppDbContext db,
        IUserDirectory users,
        IExportStore store,
        ICoverStore covers,
        ExportSettings settings,
        TimeProvider time) : IJobHandler<Payload>
    {
        public async Task HandleAsync(Payload payload, CancellationToken cancellationToken)
        {
            // A job has no request, so it acts as the person who asked, with the libraries they belong to now. The database filter does the rest.
            currentUser.Initialize(payload.UserId, await memberships.GetMembershipsAsync(payload.UserId, cancellationToken));

            var libraries = await LoadLibrariesAsync(cancellationToken);
            await store.SaveAsync(payload.ExportId, time.GetUtcNow(), async (stream, token) =>
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                await WriteItemsCsvAsync(zip, libraries, token);
                await WriteItemsJsonAsync(zip, libraries, token);
                await WriteJsonArrayAsync(zip, "locations.json", await LocationsAsync(libraries, token), token);
                await WriteJsonArrayAsync(zip, "tags.json", Tags(libraries), token);
                await WritePhotosAsync(zip, await PhotosAsync(libraries, token), token);
                await WriteJsonStreamAsync(zip, "feature-prints.json", FeaturePrintsAsync(libraries, token), token);
            }, cancellationToken);
        }

        private async Task<List<LibraryData>> LoadLibrariesAsync(CancellationToken cancellationToken)
        {
            var result = new List<LibraryData>();
            var libraries = await db.Libraries.AsNoTracking().OrderBy(l => l.Name).ThenBy(l => l.Id)
                .Select(l => new { l.Id, l.Name }).ToListAsync(cancellationToken);
            foreach (var library in libraries)
            {
                var locations = await db.Locations.AsNoTracking().Where(l => l.LibraryId == library.Id)
                    .Select(l => new { l.Id, l.ParentId, l.Name }).ToListAsync(cancellationToken);
                var tags = await db.Tags.AsNoTracking().Where(t => t.LibraryId == library.Id)
                    .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
                result.Add(new LibraryData(library.Id, library.Name, PathsOf(locations.ToDictionary(l => l.Id, l => (l.ParentId, l.Name))), tags));
            }

            return result;
        }

        private static Dictionary<Guid, string> PathsOf(Dictionary<Guid, (Guid? ParentId, string Name)> locations)
        {
            var paths = new Dictionary<Guid, string>();
            foreach (var id in locations.Keys)
            {
                var names = new List<string>();
                Guid? current = id;
                var steps = 0;
                while (current is { } step && locations.TryGetValue(step, out var location) && steps++ < 50)
                {
                    names.Add(location.Name);
                    current = location.ParentId;
                }

                names.Reverse();
                paths[id] = string.Join(" > ", names);
            }

            return paths;
        }

        private async Task<List<ExportLocation>> LocationsAsync(List<LibraryData> libraries, CancellationToken cancellationToken)
        {
            var result = new List<ExportLocation>();
            foreach (var library in libraries)
            {
                var rows = await db.Locations.AsNoTracking().Where(l => l.LibraryId == library.Id)
                    .OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ThenBy(l => l.Id)
                    .Select(l => new { l.Id, l.ParentId, l.Name, l.SortOrder }).ToListAsync(cancellationToken);
                result.AddRange(rows.Select(l => new ExportLocation(l.Id, library.Id, l.ParentId, l.Name, library.LocationPaths[l.Id], l.SortOrder)));
            }

            return result;
        }

        /// <summary>
        /// Copies each photo file into <c>photos/&lt;assetId&gt;.jpg</c>, one at a time and straight from disk to the zip (never held in memory),
        /// then writes the manifest. JPEGs are stored as they are, not compressed again. There is no size cap: a big photo set makes a big zip
        /// (about 0.5 MB a photo) and takes longer, but it stays a single streamed file that the 24-hour clean-up removes like any other.
        /// A photo whose file is missing is listed with <c>file: null</c> and the export still finishes.
        /// </summary>
        private async Task WritePhotosAsync(ZipArchive zip, List<PhotoToExport> photos, CancellationToken cancellationToken)
        {
            var manifest = new List<ExportPhoto>();
            foreach (var (photo, blobPath) in photos.Select(p => (p.Photo, p.BlobPath)))
            {
                var source = await covers.OpenReadAsync(blobPath, cancellationToken);
                if (source is null)
                {
                    manifest.Add(photo);
                    continue;
                }

                await using (source.ConfigureAwait(false))
                {
                    var name = $"photos/{photo.AssetId}.jpg";
                    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    var target = entry.Open();
                    await using (target.ConfigureAwait(false))
                    {
                        await source.CopyToAsync(target, cancellationToken);
                    }

                    manifest.Add(photo with { File = name });
                }
            }

            await WriteJsonArrayAsync(zip, "photos.json", manifest, cancellationToken);
        }

        private async Task<List<PhotoToExport>> PhotosAsync(List<LibraryData> libraries, CancellationToken cancellationToken)
        {
            var result = new List<PhotoToExport>();
            foreach (var library in libraries)
            {
                var photos = await db.Assets.AsNoTracking()
                    .Where(a => a.OwnerLibraryId == library.Id && a.Kind == AssetKind.UserPhoto)
                    .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                    .Select(a => new { a.Id, a.ContentType, a.Width, a.Height, a.CreatedAt, a.BlobPath })
                    .ToListAsync(cancellationToken);
                var assetIds = photos.Select(p => p.Id).ToList();
                var uses = (await db.Items.AsNoTracking()
                        .Where(i => i.LibraryId == library.Id && i.CustomCoverAssetId != null && assetIds.Contains(i.CustomCoverAssetId.Value))
                        .Select(i => new { AssetId = i.CustomCoverAssetId!.Value, ItemId = i.Id })
                        .ToListAsync(cancellationToken))
                    .ToLookup(u => u.AssetId, u => u.ItemId);
                result.AddRange(photos.Select(p => new PhotoToExport(
                    new ExportPhoto(p.Id, library.Id, p.ContentType, p.Width, p.Height, p.CreatedAt, uses[p.Id].Order().ToList()), p.BlobPath)));
            }

            return result;
        }

        private async IAsyncEnumerable<ExportFeaturePrint> FeaturePrintsAsync(
            List<LibraryData> libraries, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var library in libraries)
            {
                var prints = db.ItemFeaturePrints.AsNoTracking()
                    .Where(p => p.LibraryId == library.Id)
                    .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                    .Select(p => new ExportFeaturePrint(p.ItemId, p.LibraryId, p.ReleaseId, p.Revision, p.Data.Length, p.CreatedAt))
                    .AsAsyncEnumerable();
                await foreach (var print in prints.WithCancellation(cancellationToken))
                {
                    yield return print;
                }
            }
        }

        private static async Task WriteJsonStreamAsync<T>(ZipArchive zip, string name, IAsyncEnumerable<T> values, CancellationToken cancellationToken)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            var stream = entry.Open();
            await using (stream.ConfigureAwait(false))
            {
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                await using (writer.ConfigureAwait(false))
                {
                    writer.WriteStartArray();
                    await foreach (var value in values.WithCancellation(cancellationToken))
                    {
                        JsonSerializer.Serialize(writer, value, Json);
                        if (writer.BytesPending > 64 * 1024)
                        {
                            await writer.FlushAsync(cancellationToken);
                        }
                    }

                    writer.WriteEndArray();
                }
            }
        }

        private static List<ExportTag> Tags(List<LibraryData> libraries) =>
            libraries
                .SelectMany(l => l.TagNames
                    .OrderBy(t => t.Value, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Key)
                    .Select(t => new ExportTag(t.Key, l.Id, t.Value)))
                .ToList();

        private async Task WriteItemsCsvAsync(ZipArchive zip, List<LibraryData> libraries, CancellationToken cancellationToken)
        {
            var entry = zip.CreateEntry("items.csv", CompressionLevel.Optimal);
            var stream = entry.Open();
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream, Utf8WithBom);
                await using (writer.ConfigureAwait(false))
                {
                    await writer.WriteAsync(ExportCsv.Row(CsvHeader) + "\r\n");
                    await foreach (var item in ReadItemsAsync(libraries, cancellationToken))
                    {
                        await writer.WriteAsync(ExportCsv.Row(
                        [
                            item.Library,
                            item.Title,
                            item.Kind,
                            item.Format,
                            item.Platform,
                            item.Year?.ToString(CultureInfo.InvariantCulture),
                            item.Status,
                            item.Condition,
                            item.Location,
                            string.Join("; ", item.Tags),
                            item.Notes,
                            item.AcquiredOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            string.Join("; ", item.Identifiers.Select(i => $"{i.Type}:{i.Value}")),
                            item.AddedBy,
                            item.AddedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        ]) + "\r\n");
                    }
                }
            }
        }

        private async Task WriteItemsJsonAsync(ZipArchive zip, List<LibraryData> libraries, CancellationToken cancellationToken)
        {
            var entry = zip.CreateEntry("items.json", CompressionLevel.Optimal);
            var stream = entry.Open();
            await using (stream.ConfigureAwait(false))
            {
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                await using (writer.ConfigureAwait(false))
                {
                    writer.WriteStartArray();
                    await foreach (var item in ReadItemsAsync(libraries, cancellationToken))
                    {
                        JsonSerializer.Serialize(writer, item, Json);
                        if (writer.BytesPending > 64 * 1024)
                        {
                            await writer.FlushAsync(cancellationToken);
                        }
                    }

                    writer.WriteEndArray();
                }
            }
        }

        private static async Task WriteJsonArrayAsync<T>(ZipArchive zip, string name, List<T> values, CancellationToken cancellationToken)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            var stream = entry.Open();
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, values, Json, cancellationToken);
            }
        }

        private async IAsyncEnumerable<ExportItem> ReadItemsAsync(
            List<LibraryData> libraries, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var library in libraries)
            {
                var after = Guid.Empty;
                while (true)
                {
                    var last = after;
                    var rows = await (
                        from i in db.Items.AsNoTracking()
                        where i.LibraryId == library.Id && i.DeletedAt == null && i.Id > last
                        join r in db.Releases on i.ReleaseId equals r.Id
                        join w in db.Works on r.WorkId equals w.Id
                        join p in db.Platforms on r.PlatformId equals p.Id into platforms
                        from p in platforms.DefaultIfEmpty()
                        orderby i.Id
                        select new Row
                        {
                            Id = i.Id,
                            ReleaseId = i.ReleaseId,
                            WorkId = w.Id,
                            WorkTitle = w.Title,
                            CustomTitle = i.CustomTitle,
                            Kind = w.Kind,
                            Format = r.Format,
                            Platform = p == null ? null : p.Name,
                            ReleaseDate = r.ReleaseDate,
                            OriginalYear = w.OriginalYear,
                            Status = i.Status,
                            Condition = i.Condition,
                            Completeness = i.Completeness,
                            LocationId = i.LocationId,
                            Notes = i.Notes,
                            AcquiredOn = i.AcquiredOn,
                            LentTo = i.LentTo,
                            LentOn = i.LentOn,
                            CreatedBy = i.CreatedBy,
                            CreatedAt = i.CreatedAt,
                            UpdatedAt = i.UpdatedAt,
                        })
                        .Take(settings.BatchSize)
                        .ToListAsync(cancellationToken);
                    if (rows.Count == 0)
                    {
                        break;
                    }

                    settings.BatchObserved?.Invoke(rows.Count);
                    after = rows[^1].Id;

                    var itemIds = rows.Select(r => r.Id).ToList();
                    var tagLinks = (await db.ItemTags.AsNoTracking().Where(t => t.LibraryId == library.Id && itemIds.Contains(t.ItemId))
                            .Select(t => new { t.ItemId, t.TagId }).ToListAsync(cancellationToken))
                        .ToLookup(t => t.ItemId, t => t.TagId);
                    var releaseIds = rows.Select(r => r.ReleaseId).Distinct().ToList();
                    // The shared barcodes and the ones this library taught, not a barcode only another library taught.
                    var identifiers = (await db.ReleaseIdentifiers.AsNoTracking().UsableBy(library.Id).Where(i => releaseIds.Contains(i.ReleaseId))
                            .OrderBy(i => i.Type).ThenBy(i => i.Value)
                            .Select(i => new { i.ReleaseId, i.Type, i.Value }).ToListAsync(cancellationToken))
                        .ToLookup(i => i.ReleaseId, i => new ExportIdentifier(EnumText(i.Type), i.Value));
                    var names = await users.DisplayNamesAsync(rows.Select(r => r.CreatedBy).Distinct().ToList(), cancellationToken);

                    foreach (var row in rows)
                    {
                        var tagIds = tagLinks[row.Id]
                            .Where(library.TagNames.ContainsKey)
                            .OrderBy(id => library.TagNames[id], StringComparer.OrdinalIgnoreCase).ThenBy(id => id)
                            .ToList();
                        string? locationPath = null;
                        if (row.LocationId is { } locationId)
                        {
                            library.LocationPaths.TryGetValue(locationId, out locationPath);
                        }

                        yield return new ExportItem(
                            row.Id,
                            library.Id,
                            library.Name,
                            row.ReleaseId,
                            row.WorkId,
                            row.CustomTitle ?? TitleCaser.ToTitleCase(row.WorkTitle),
                            row.CustomTitle,
                            EnumText(row.Kind),
                            EnumText(row.Format),
                            row.Platform,
                            row.ReleaseDate?.Year ?? row.OriginalYear,
                            EnumText(row.Status),
                            row.Condition is { } condition ? EnumText(condition) : null,
                            row.Completeness is { } completeness ? EnumText(completeness) : null,
                            row.LocationId,
                            locationPath,
                            tagIds,
                            tagIds.Select(id => library.TagNames[id]).ToList(),
                            row.Notes,
                            row.AcquiredOn,
                            row.LentTo,
                            row.LentOn,
                            identifiers[row.ReleaseId].ToList(),
                            names.GetValueOrDefault(row.CreatedBy),
                            row.CreatedAt,
                            row.UpdatedAt);
                    }

                    if (rows.Count < settings.BatchSize)
                    {
                        break;
                    }
                }
            }
        }

        private static string EnumText<T>(T value) where T : struct, Enum => JsonSerializer.Serialize(value, Json).Trim('"');
    }
}
