using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Tests.Exports;

public class ReadExportTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>Builds a zip the way BuildExport writes one: the JSON manifests plus the photo files.</summary>
    private static MemoryStream WriteZip(Action<ZipArchive> write)
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            write(archive);
        }

        zip.Position = 0;
        return zip;
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(text);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    [Fact]
    public void Reading_a_zip_returns_every_item_location_tag_photo_and_print()
    {
        var itemId = Guid.CreateVersion7();
        var libraryId = Guid.CreateVersion7();
        var releaseId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var tagId = Guid.CreateVersion7();
        var assetId = Guid.CreateVersion7();
        byte[] photo = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];
        using var zip = WriteZip(archive =>
        {
            WriteEntry(archive, "items.json", $$"""
                [{"id":"{{itemId}}","libraryId":"{{libraryId}}","library":"Test library","releaseId":"{{releaseId}}","workId":"{{Guid.Empty}}","title":"Chrono Trigger","customTitle":null,"kind":"game","format":"game_cartridge","platform":null,"year":null,"status":"owned","condition":null,"completeness":null,"locationId":"{{locationId}}","location":"Living room","tagIds":["{{tagId}}"],"tags":["Favourites"],"notes":null,"acquiredOn":null,"lentTo":null,"lentOn":null,"identifiers":[],"addedBy":null,"addedAt":"2026-03-04T05:06:07+00:00","updatedAt":"2026-03-04T05:06:07+00:00"}]
                """);
            WriteEntry(archive, "locations.json", $$"""
                [{"id":"{{locationId}}","libraryId":"{{libraryId}}","parentId":null,"name":"Living room","path":"Living room","sortOrder":1024}]
                """);
            WriteEntry(archive, "tags.json", $$"""
                [{"id":"{{tagId}}","libraryId":"{{libraryId}}","name":"Favourites"}]
                """);
            WriteEntry(archive, "photos.json", $$"""
                [{"assetId":"{{assetId}}","libraryId":"{{libraryId}}","contentType":"image/jpeg","width":4,"height":3,"createdAt":"2026-03-04T05:06:07+00:00","itemIds":["{{itemId}}"],"file":"photos/{{assetId}}.jpg"}]
                """);
            WriteEntry(archive, "feature-prints.json", $$"""
                [{"itemId":"{{itemId}}","libraryId":"{{libraryId}}","releaseId":"{{releaseId}}","revision":2,"bytes":9,"createdAt":"2026-03-04T05:06:07+00:00"}]
                """);
            WriteEntry(archive, $"photos/{assetId}.jpg", photo);
        });

        var snapshot = ReadExport.Read(zip);

        var item = snapshot.Items.ShouldHaveSingleItem();
        item.Id.ShouldBe(itemId);
        item.LibraryId.ShouldBe(libraryId);
        item.Library.ShouldBe("Test library");
        item.Kind.ShouldBe(WorkKind.Game);
        item.Format.ShouldBe(ReleaseFormat.GameCartridge);
        item.Status.ShouldBe(ItemStatus.Owned);
        item.LocationId.ShouldBe(locationId);
        item.Location.ShouldBe("Living room");
        item.TagIds.ShouldBe([tagId]);
        item.Tags.ShouldBe(["Favourites"]);
        item.AddedAt.ShouldBe(At);
        var location = snapshot.Locations.ShouldHaveSingleItem();
        location.Id.ShouldBe(locationId);
        location.Path.ShouldBe("Living room");
        location.SortOrder.ShouldBe(1024);
        var tag = snapshot.Tags.ShouldHaveSingleItem();
        tag.Id.ShouldBe(tagId);
        tag.Name.ShouldBe("Favourites");
        var picture = snapshot.Photos.ShouldHaveSingleItem();
        picture.AssetId.ShouldBe(assetId);
        picture.Data.ShouldBe(photo);
        picture.ItemIds.ShouldBe([itemId]);
        var print = snapshot.FeaturePrints.ShouldHaveSingleItem();
        print.ItemId.ShouldBe(itemId);
        print.Revision.ShouldBe(2);
        print.Bytes.ShouldBe(9);
    }

    [Fact]
    public void A_zip_without_items_json_is_rejected_naming_it()
    {
        using var zip = WriteZip(archive => WriteEntry(archive, "tags.json", "[]"));

        Should.Throw<ExportFormatException>(() => ReadExport.Read(zip)).Entry.ShouldBe("items.json");
    }

    [Fact]
    public void A_zip_with_broken_json_is_rejected()
    {
        using var zip = WriteZip(archive => WriteEntry(archive, "items.json", "[{oops"));

        Should.Throw<JsonException>(() => ReadExport.Read(zip));
    }

    [Fact]
    public void A_photo_manifest_entry_without_its_file_reads_null_bytes()
    {
        var assetId = Guid.CreateVersion7();
        using var zip = WriteZip(archive =>
        {
            WriteEntry(archive, "items.json", "[]");
            WriteEntry(archive, "locations.json", "[]");
            WriteEntry(archive, "tags.json", "[]");
            WriteEntry(archive, "photos.json", $$"""
                [{"assetId":"{{assetId}}","libraryId":"{{Guid.Empty}}","contentType":"image/jpeg","width":null,"height":null,"createdAt":"2026-03-04T05:06:07+00:00","itemIds":[],"file":"photos/{{assetId}}.jpg"}]
                """);
            WriteEntry(archive, "feature-prints.json", "[]");
        });

        var picture = ReadExport.Read(zip).Photos.ShouldHaveSingleItem();

        picture.Data.ShouldBeNull();
    }

    [Fact]
    public void A_photo_listed_without_a_file_reads_null_bytes()
    {
        var assetId = Guid.CreateVersion7();
        using var zip = WriteZip(archive =>
        {
            WriteEntry(archive, "items.json", "[]");
            WriteEntry(archive, "locations.json", "[]");
            WriteEntry(archive, "tags.json", "[]");
            WriteEntry(archive, "photos.json", $$"""
                [{"assetId":"{{assetId}}","libraryId":"{{Guid.Empty}}","contentType":"image/jpeg","width":null,"height":null,"createdAt":"2026-03-04T05:06:07+00:00","itemIds":[],"file":null}]
                """);
            WriteEntry(archive, "feature-prints.json", "[]");
        });

        ReadExport.Read(zip).Photos.ShouldHaveSingleItem().Data.ShouldBeNull();
    }
}
