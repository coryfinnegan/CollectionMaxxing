using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Exports;

/// <summary>
/// The export → import round trip (#361): seed a rich library, export it over HTTP, read the zip back with
/// <see cref="ReadExport"/> (the import side) and prove nothing was lost: every item field, the cover bytes and
/// their item links, the locations, tags, identifiers and feature prints. A future restore endpoint will parse
/// with the same reader and write these rows back.
/// </summary>
public class ExportRoundTripTests(ApiFixture api) : ApiTest(api)
{
    // A day ahead of the real clock, like ExportTests: the sign-in certificate is minted on the real clock so must
    // be valid at the fake one too, and jobs due "tomorrow" are never picked up by other test hosts sharing this database.
    private static readonly DateTimeOffset Start = new(DateTimeOffset.UtcNow.AddDays(1).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        public Host(ApiFixture api)
        {
            Folder = Path.Combine(Path.GetTempPath(), "cm-roundtrip-tests", Guid.NewGuid().ToString("N"));
            PhotosFolder = Folder + "-photos";
            Time = new FakeTimeProvider(Start);
            _factory = api.Factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Exports:Path"] = Folder, ["Catalog:CoversRoot"] = PhotosFolder }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Time);
                    foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobWorker)).ToList())
                    {
                        services.Remove(worker);
                    }
                });
            });
            Api = api;
        }

        public ApiFixture Api { get; }

        public string Folder { get; }

        public string PhotosFolder { get; }

        public FakeTimeProvider Time { get; }

        public HttpClient ClientFor(Guid userId)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
            return client;
        }

        public async Task RunJobsAsync()
        {
            var runner = _factory.Services.GetRequiredService<JobRunner>();
            while (await runner.RunNextAsync("test-worker", CancellationToken.None))
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }

            if (Directory.Exists(PhotosFolder))
            {
                Directory.Delete(PhotosFolder, recursive: true);
            }
        }
    }

    private sealed record Seeded(TestLibrary Library, Guid Kept, Guid Deleted, Guid ReleaseId, Guid Photo, byte[] PhotoBytes);

    /// <summary>One library with a Room › Shelf location, a tag, a rich item, a soft-deleted one, a cover photo and a feature print.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var library = await CreateLibraryAsync();
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Living room");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf A", room);
        var tag = await TestData.CreateTagAsync(Db, library.Id, "Favourites");

        await using var context = Db.CreateContext();
        var platform = new Platform { Id = Guid.CreateVersion7(), Name = "Super Nintendo", ShortName = "SNES" };
        context.Platforms.Add(platform);
        var work = new Work
        {
            Id = Guid.CreateVersion7(),
            Kind = WorkKind.Game,
            Title = "Chrono Trigger",
            SortTitle = "Chrono Trigger",
            NormalizedTitle = "chrono trigger",
            OriginalYear = 1995,
            CreatedAt = Start,
        };
        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = work.Id,
            Format = ReleaseFormat.GameCartridge,
            PlatformId = platform.Id,
            ReleaseDate = new DateOnly(1995, 3, 11),
            Source = ReleaseSource.Provider,
            CreatedAt = Start,
        };
        context.Works.Add(work);
        context.Releases.Add(release);
        context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = release.Id, Type = IdentifierType.Gtin13, Value = "0045496830067" });

        var kept = new Item
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ReleaseId = release.Id,
            CustomTitle = "Chrono Trigger",
            Status = ItemStatus.Lent,
            Condition = ItemCondition.VeryGood,
            Completeness = ItemCompleteness.CartOnly,
            LocationId = shelf,
            Notes = "Bought at a fair",
            AcquiredOn = new DateOnly(2001, 5, 6),
            LentTo = "Sam",
            LentOn = new DateOnly(2026, 1, 2),
            CoverChoice = CoverChoice.Release,
            AddedVia = AddedVia.Manual,
            CreatedAt = Start,
            CreatedBy = library.OwnerId,
            UpdatedAt = Start,
        };
        var deleted = new Item
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ReleaseId = release.Id,
            Status = ItemStatus.Owned,
            CoverChoice = CoverChoice.Release,
            AddedVia = AddedVia.Manual,
            CreatedAt = Start,
            CreatedBy = library.OwnerId,
            UpdatedAt = Start,
            DeletedAt = Start,
        };
        context.Items.AddRange(kept, deleted);
        context.ItemTags.Add(new ItemTag { LibraryId = library.Id, ItemId = kept.Id, TagId = tag });
        context.ItemFeaturePrints.Add(new ItemFeaturePrint
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ItemId = kept.Id,
            ReleaseId = release.Id,
            Revision = 2,
            Data = [1, 2, 3, 4, 5, 6, 7, 8, 9],
            CreatedAt = Start,
        });
        await context.SaveChangesAsync();

        byte[] photoBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 0xFF, 0xD9];
        var photo = await AddPhotoAsync(library.Id, photoBytes, kept.Id);
        return new Seeded(library, kept.Id, deleted.Id, release.Id, photo, photoBytes);
    }

    private async Task<Guid> AddPhotoAsync(Guid libraryId, byte[] bytes, Guid itemId)
    {
        // The covers folder is per-host, so the file is written by the caller; this only stores the asset row.
        var id = Guid.CreateVersion7();
        await using var context = Db.CreateContext();
        context.Assets.Add(new Asset
        {
            Id = id,
            Kind = AssetKind.UserPhoto,
            BlobPath = $"photos/{id:N}.jpg",
            ContentType = "image/jpeg",
            Width = 4,
            Height = 3,
            Sha256 = new string('a', 64),
            OwnerLibraryId = libraryId,
            CreatedAt = Start,
        });
        await context.SaveChangesAsync();
        await context.Items.IgnoreQueryFilters().Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.CustomCoverAssetId, id));
        return id;
    }

    [Fact]
    public async Task Export_then_read_back_preserves_items_covers_and_metadata()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);
        var file = Path.Combine(host.PhotosFolder, "photos", $"{seeded.Photo:N}.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, seeded.PhotoBytes);

        var client = host.ClientFor(seeded.Library.OwnerId);
        var started = await ReadJsonAsync(await client.GetAsync("/api/v1/me/export"));
        await host.RunJobsAsync();
        var poll = await client.GetAsync($"/api/v1/me/export/{started.GetProperty("jobId").GetGuid()}");
        poll.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(poll);
        body.GetProperty("status").GetString().ShouldBe("ready");
        var zip = await (await client.GetAsync(body.GetProperty("downloadUrl").GetString())).Content.ReadAsByteArrayAsync();

        ReadExport.Snapshot snapshot;
        using (var stream = new MemoryStream(zip))
        {
            snapshot = ReadExport.Read(stream);
        }

        var item = snapshot.Items.ShouldHaveSingleItem();
        item.Id.ShouldBe(seeded.Kept);
        item.LibraryId.ShouldBe(seeded.Library.Id);
        item.Library.ShouldBe("Test library");
        item.ReleaseId.ShouldBe(seeded.ReleaseId);
        item.Title.ShouldBe("Chrono Trigger");
        item.CustomTitle.ShouldBe("Chrono Trigger");
        item.Kind.ShouldBe(WorkKind.Game);
        item.Format.ShouldBe(ReleaseFormat.GameCartridge);
        item.Platform.ShouldBe("Super Nintendo");
        item.Year.ShouldBe(1995);
        item.Status.ShouldBe(ItemStatus.Lent);
        item.Condition.ShouldBe(ItemCondition.VeryGood);
        item.Completeness.ShouldBe(ItemCompleteness.CartOnly);
        item.Location.ShouldBe("Living room > Shelf A");
        item.Tags.ShouldBe(["Favourites"]);
        item.Notes.ShouldBe("Bought at a fair");
        item.AcquiredOn.ShouldBe(new DateOnly(2001, 5, 6));
        item.LentTo.ShouldBe("Sam");
        item.LentOn.ShouldBe(new DateOnly(2026, 1, 2));
        item.Identifiers.ShouldBe([new ReadExport.SnapshotIdentifier("gtin13", "0045496830067")]);
        item.AddedBy.ShouldBe("Test user");
        item.AddedAt.ShouldBe(Start);
        item.UpdatedAt.ShouldBe(Start);

        var locations = await TestData.ReadLocationsAsync(Db, seeded.Library.Id);
        snapshot.Locations.Count.ShouldBe(2);
        foreach (var location in snapshot.Locations)
        {
            var row = locations.Single(l => l.Id == location.Id);
            location.LibraryId.ShouldBe(seeded.Library.Id);
            location.ParentId.ShouldBe(row.ParentId);
            location.Name.ShouldBe(row.Name);
            location.SortOrder.ShouldBe(row.SortOrder);
        }

        snapshot.Locations.Select(l => l.Path).Order().ShouldBe(["Living room", "Living room > Shelf A"]);
        item.LocationId.ShouldBe(snapshot.Locations.Single(l => l.Path == "Living room > Shelf A").Id);

        var tag = snapshot.Tags.ShouldHaveSingleItem();
        tag.LibraryId.ShouldBe(seeded.Library.Id);
        tag.Name.ShouldBe("Favourites");
        item.TagIds.ShouldBe([tag.Id]);

        var picture = snapshot.Photos.ShouldHaveSingleItem();
        picture.AssetId.ShouldBe(seeded.Photo);
        picture.LibraryId.ShouldBe(seeded.Library.Id);
        picture.ContentType.ShouldBe("image/jpeg");
        picture.Width.ShouldBe(4);
        picture.Height.ShouldBe(3);
        picture.CreatedAt.ShouldBe(Start);
        picture.File.ShouldBe($"photos/{seeded.Photo}.jpg");
        picture.Data.ShouldBe(seeded.PhotoBytes);
        picture.ItemIds.ShouldBe([seeded.Kept]);

        var print = snapshot.FeaturePrints.ShouldHaveSingleItem();
        print.ItemId.ShouldBe(seeded.Kept);
        print.LibraryId.ShouldBe(seeded.Library.Id);
        print.ReleaseId.ShouldBe(seeded.ReleaseId);
        print.Revision.ShouldBe(2);
        print.Bytes.ShouldBe(9);
        print.CreatedAt.ShouldBe(Start);

        // The soft-deleted item never leaves the building.
        JsonSerializer.Serialize(snapshot).ShouldNotContain(seeded.Deleted.ToString());
    }
}
