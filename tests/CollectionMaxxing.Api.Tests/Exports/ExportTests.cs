using System.IO.Compression;
using System.Net;
using System.Text;
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
/// "Export my data" (LIB-2b), from the caller's side: start, poll, download. The app runs for real except that time is fake, the
/// export folder is a temporary one and the background worker does not poll by itself: the test runs the due jobs when it says so.
/// </summary>
public class ExportTests(ApiFixture api) : ApiTest(api)
{
    // A day ahead of the real clock: the sign-in certificate is minted on the real clock so must be valid at the fake one too, and jobs due "tomorrow" are never picked up by the other test hosts' real-time workers sharing this database.
    private static readonly DateTimeOffset Start = new(DateTimeOffset.UtcNow.AddDays(1).Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        public Host(ApiFixture api, Action<ExportSettings>? tune = null)
        {
            Folder = Path.Combine(Path.GetTempPath(), "cm-export-tests", Guid.NewGuid().ToString("N"));
            PhotosFolder = Folder + "-photos";
            Time = new FakeTimeProvider(Start);
            Settings = new ExportSettings();
            tune?.Invoke(Settings);
            _factory = api.Factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Exports:Path"] = Folder, ["Catalog:CoversRoot"] = PhotosFolder }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Time);
                    services.RemoveAll<ExportSettings>();
                    services.AddSingleton(Settings);
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

        /// <summary>Where the stored photo files are (the covers folder the app reads photos from).</summary>
        public string PhotosFolder { get; }

        public FakeTimeProvider Time { get; }

        public ExportSettings Settings { get; }

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

        public async Task RunCleanupAsync()
        {
            await _factory.Services.GetRequiredService<RecurringJobScheduler>().EnqueueDueAsync(CancellationToken.None);
            await RunJobsAsync();
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

    private static async Task<JsonElement> StartAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/me/export");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return await ReadJsonAsync(response);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PollAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/v1/me/export/{id}");
        return (response.StatusCode, await ReadJsonAsync(response));
    }

    /// <summary>Starts an export, runs the job and downloads the zip the way the app would.</summary>
    private static async Task<Dictionary<string, string>> ExportAsync(Host host, Guid userId)
    {
        var client = host.ClientFor(userId);
        var started = await StartAsync(client);
        await host.RunJobsAsync();
        var (status, body) = await PollAsync(client, started.GetProperty("jobId").GetGuid());
        status.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("status").GetString().ShouldBe("ready");
        var download = await client.GetAsync(body.GetProperty("downloadUrl").GetString());
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        return Unzip(await download.Content.ReadAsByteArrayAsync());
    }

    private static Dictionary<string, string> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    private static Dictionary<string, byte[]> UnzipBytes(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
    }

    private static async Task<Dictionary<string, byte[]>> ExportBytesAsync(Host host, Guid userId)
    {
        var client = host.ClientFor(userId);
        var started = await StartAsync(client);
        await host.RunJobsAsync();
        var (_, body) = await PollAsync(client, started.GetProperty("jobId").GetGuid());
        body.GetProperty("status").GetString().ShouldBe("ready");
        return UnzipBytes(await (await client.GetAsync(body.GetProperty("downloadUrl").GetString())).Content.ReadAsByteArrayAsync());
    }

    /// <summary>Stores a photo file in the host's photo folder and the asset row that points at it. <paramref name="bytes"/> null leaves the file out.</summary>
    private async Task<Guid> AddPhotoAsync(Host host, Guid libraryId, byte[]? bytes, Guid? itemId = null)
    {
        var id = Guid.CreateVersion7();
        var blobPath = $"photos/{id:N}.jpg";
        if (bytes is not null)
        {
            var file = Path.Combine(host.PhotosFolder, "photos", $"{id:N}.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, bytes);
        }

        await using var context = Db.CreateContext();
        context.Assets.Add(new Asset
        {
            Id = id,
            Kind = AssetKind.UserPhoto,
            BlobPath = blobPath,
            ContentType = "image/jpeg",
            Width = 4,
            Height = 3,
            Sha256 = new string('a', 64),
            OwnerLibraryId = libraryId,
            CreatedAt = Start,
        });
        await context.SaveChangesAsync();
        if (itemId is { } item)
        {
            await context.Items.IgnoreQueryFilters().Where(i => i.Id == item).ExecuteUpdateAsync(set => set.SetProperty(i => i.CustomCoverAssetId, id));
        }

        return id;
    }

    [Fact]
    public async Task The_zip_holds_the_photo_files_of_my_libraries_and_the_manifest_points_at_them()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);
        byte[] picture = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 0xFF, 0xD9];
        var photo = await AddPhotoAsync(host, seeded.Library.Id, picture, seeded.Kept);

        var files = await ExportBytesAsync(host, seeded.Library.OwnerId);

        files[$"photos/{photo}.jpg"].ShouldBe(picture);
        var manifest = JsonDocument.Parse(files["photos.json"]).RootElement.EnumerateArray().ShouldHaveSingleItem();
        manifest.GetProperty("assetId").GetGuid().ShouldBe(photo);
        manifest.GetProperty("file").GetString().ShouldBe($"photos/{photo}.jpg");
        manifest.GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid()).ShouldBe([seeded.Kept]);
    }

    [Fact]
    public async Task Another_persons_photo_files_never_reach_my_zip()
    {
        var mine = await SeedAsync();
        var other = await CreateLibraryAsync();
        await using var host = new Host(Api);
        var mineId = await AddPhotoAsync(host, mine.Library.Id, [1, 2, 3]);
        var theirs = await AddPhotoAsync(host, other.Id, [9, 9, 9]);

        var files = await ExportBytesAsync(host, mine.Library.OwnerId);

        files.Keys.Where(k => k.StartsWith("photos/", StringComparison.Ordinal)).ShouldBe([$"photos/{mineId}.jpg"]);
        Encoding.UTF8.GetString(files["photos.json"]).ShouldNotContain(theirs.ToString());
    }

    [Fact]
    public async Task A_photo_whose_file_is_gone_is_listed_without_a_file_and_the_export_still_finishes()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);
        var lost = await AddPhotoAsync(host, seeded.Library.Id, bytes: null);
        var kept = await AddPhotoAsync(host, seeded.Library.Id, [7, 7]);

        var files = await ExportBytesAsync(host, seeded.Library.OwnerId);

        files.Keys.Where(k => k.StartsWith("photos/", StringComparison.Ordinal)).ShouldBe([$"photos/{kept}.jpg"]);
        var manifest = JsonDocument.Parse(files["photos.json"]).RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("assetId").GetGuid(), e => e.GetProperty("file"));
        manifest[lost].ValueKind.ShouldBe(JsonValueKind.Null);
        manifest[kept].GetString().ShouldBe($"photos/{kept}.jpg");
    }

    [Fact]
    public async Task The_photo_files_leave_with_the_zip_when_the_export_expires()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);
        await AddPhotoAsync(host, seeded.Library.Id, [1, 2, 3]);
        var client = host.ClientFor(seeded.Library.OwnerId);
        var id = (await StartAsync(client)).GetProperty("jobId").GetGuid();
        await host.RunJobsAsync();
        Directory.GetFiles(host.Folder, "*.zip").Length.ShouldBe(1);

        host.Time.Advance(host.Settings.Retention + TimeSpan.FromHours(1));
        await host.RunCleanupAsync();

        Directory.EnumerateFiles(host.Folder, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        (await PollAsync(client, id)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>A small RFC 4180 reader, so the tests check the CSV the way a spreadsheet would read it.</summary>
    private static List<Dictionary<string, string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                i++;
            }
            else
            {
                cell.Append(c);
            }
        }

        var header = rows[0];
        return rows.Skip(1).Select(r => header.Zip(r).ToDictionary(p => p.First, p => p.Second)).ToList();
    }

    private sealed record Seeded(TestLibrary Library, Guid Kept, Guid Deleted, Guid Shelf, Guid Tag);

    /// <summary>One library with a Room › Shelf location, a tag, a rich item and a soft-deleted one.</summary>
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

        var kept = NewItem(library, release.Id, "Chrono Trigger");
        kept.Status = ItemStatus.Lent;
        kept.Condition = ItemCondition.VeryGood;
        kept.Completeness = ItemCompleteness.CartOnly;
        kept.LocationId = shelf;
        kept.Notes = "Bought at a fair";
        kept.AcquiredOn = new DateOnly(2001, 5, 6);
        kept.LentTo = "Sam";
        kept.LentOn = new DateOnly(2026, 1, 2);
        var deleted = NewItem(library, release.Id, "Binned");
        deleted.DeletedAt = Start;
        context.Items.AddRange(kept, deleted);
        context.ItemTags.Add(new ItemTag { LibraryId = library.Id, ItemId = kept.Id, TagId = tag });
        await context.SaveChangesAsync();
        return new Seeded(library, kept.Id, deleted.Id, shelf, tag);
    }

    private static Item NewItem(TestLibrary library, Guid releaseId, string? customTitle = null) => new()
    {
        Id = Guid.CreateVersion7(),
        LibraryId = library.Id,
        ReleaseId = releaseId,
        Status = ItemStatus.Owned,
        CoverChoice = CoverChoice.Release,
        AddedVia = AddedVia.Manual,
        CreatedAt = Start,
        CreatedBy = library.OwnerId,
        UpdatedAt = Start,
        CustomTitle = customTitle,
    };

    [Fact]
    public async Task The_csv_has_every_field_of_every_item_and_leaves_out_deleted_ones()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);

        var files = await ExportAsync(host, seeded.Library.OwnerId);

        files.Keys.OrderBy(k => k).ShouldBe(["feature-prints.json", "items.csv", "items.json", "locations.json", "photos.json", "tags.json"]);
        var rows = ParseCsv(files["items.csv"]);
        var row = rows.ShouldHaveSingleItem();
        row["library"].ShouldBe("Test library");
        row["title"].ShouldBe("Chrono Trigger");
        row["kind"].ShouldBe("game");
        row["format"].ShouldBe("game_cartridge");
        row["platform"].ShouldBe("Super Nintendo");
        row["year"].ShouldBe("1995");
        row["status"].ShouldBe("lent");
        row["condition"].ShouldBe("very_good");
        row["location"].ShouldBe("Living room > Shelf A");
        row["tags"].ShouldBe("Favourites");
        row["notes"].ShouldBe("Bought at a fair");
        row["acquired_on"].ShouldBe("2001-05-06");
        row["identifiers"].ShouldBe("gtin13:0045496830067");
        row["added_by"].ShouldBe("Test user");
        row["added_at"].ShouldBe(Start.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_json_keeps_the_ids_so_a_second_export_matches_the_first()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);

        var first = await ExportAsync(host, seeded.Library.OwnerId);
        host.Time.Advance(TimeSpan.FromHours(1));
        var second = await ExportAsync(host, seeded.Library.OwnerId);

        first["items.json"].ShouldBe(second["items.json"]);
        var items = JsonDocument.Parse(first["items.json"]).RootElement;
        var item = items.EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("id").GetGuid().ShouldBe(seeded.Kept);
        item.GetProperty("libraryId").GetGuid().ShouldBe(seeded.Library.Id);
        item.GetProperty("locationId").GetGuid().ShouldBe(seeded.Shelf);
        item.GetProperty("tagIds")[0].GetGuid().ShouldBe(seeded.Tag);
        item.GetProperty("lentTo").GetString().ShouldBe("Sam");
        first["items.json"].ShouldNotContain(seeded.Deleted.ToString());

        var locations = JsonDocument.Parse(first["locations.json"]).RootElement.EnumerateArray().ToList();
        locations.Select(l => l.GetProperty("path").GetString()).ShouldBe(["Living room", "Living room > Shelf A"]);
        var tags = JsonDocument.Parse(first["tags.json"]).RootElement.EnumerateArray().ShouldHaveSingleItem();
        tags.GetProperty("id").GetGuid().ShouldBe(seeded.Tag);
        tags.GetProperty("name").GetString().ShouldBe("Favourites");
    }

    [Fact]
    public async Task A_viewer_can_export_their_view_of_the_library()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);

        var files = await ExportAsync(host, seeded.Library.ViewerId);

        ParseCsv(files["items.csv"]).ShouldHaveSingleItem()["title"].ShouldBe("Chrono Trigger");
    }

    [Fact]
    public async Task Cells_that_start_like_a_formula_are_neutralised_and_commas_quotes_and_newlines_survive()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var titles = new[] { "=HYPERLINK(\"http://example.test\")", "+1+1", "-2+3", "@SUM(A1)", "Plain, with \"quotes\"" };
        const string notes = "line one\nline two, \"quoted\"\r\nline three";
        await using (var context = Db.CreateContext())
        {
            foreach (var title in titles)
            {
                var item = NewItem(library, releaseId, title);
                item.Notes = title.StartsWith('=') ? notes : "=1+1";
                context.Items.Add(item);
            }

            await context.SaveChangesAsync();
        }

        await using var host = new Host(Api);
        var rows = ParseCsv((await ExportAsync(host, library.OwnerId))["items.csv"]);

        rows.Select(r => r["title"]).Order().ShouldBe(new[]
        {
            "'+1+1", "'-2+3", "'=HYPERLINK(\"http://example.test\")", "'@SUM(A1)", "Plain, with \"quotes\"",
        }.Order());
        rows.Single(r => r["title"].StartsWith("'=", StringComparison.Ordinal))["notes"].ShouldBe(notes);
        rows.Where(r => r["title"] != "'=HYPERLINK(\"http://example.test\")").ShouldAllBe(r => r["notes"] == "'=1+1");
    }

    [Fact]
    public async Task Another_user_cannot_see_poll_or_download_an_export_that_is_not_theirs()
    {
        var seeded = await SeedAsync();
        var other = await CreateLibraryAsync();
        await using var host = new Host(Api);
        var owner = host.ClientFor(seeded.Library.OwnerId);
        var started = await StartAsync(owner);
        var id = started.GetProperty("jobId").GetGuid();
        await host.RunJobsAsync();
        var link = (await PollAsync(owner, id)).Body.GetProperty("downloadUrl").GetString()!;

        var stranger = host.ClientFor(other.Library().OwnerId);

        (await stranger.GetAsync($"/api/v1/me/export/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.NotFound); // A's link in B's hands
        (await stranger.GetAsync($"/api/v1/me/export/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.GetAsync($"/api/v1/me/export/{id}/download")).StatusCode.ShouldBe(HttpStatusCode.NotFound); // no token: a guessed id is not enough
        (await owner.GetAsync($"/api/v1/me/export/{id}/download?token=abc")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await host.Api.CreateClient().GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // the link alone does not sign anyone in
        (await owner.GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_library_the_caller_has_left_is_not_in_the_export()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);
        var client = host.ClientFor(seeded.Library.EditorId);
        var started = await StartAsync(client);
        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.UserId == seeded.Library.EditorId).ExecuteDeleteAsync();
        }

        await host.RunJobsAsync();
        var link = (await PollAsync(client, started.GetProperty("jobId").GetGuid())).Body.GetProperty("downloadUrl").GetString()!;
        var files = Unzip(await (await client.GetAsync(link)).Content.ReadAsByteArrayAsync());

        ParseCsv(files["items.csv"]).ShouldBeEmpty();
        files["items.json"].Trim().ShouldBe("[]");
        files["locations.json"].ShouldBe("[]");
    }

    [Fact]
    public async Task Asking_again_while_an_export_is_waiting_returns_the_same_one()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);
        var client = host.ClientFor(seeded.Library.OwnerId);

        var first = await StartAsync(client);
        var second = await StartAsync(client);

        second.GetProperty("jobId").GetGuid().ShouldBe(first.GetProperty("jobId").GetGuid());
        first.GetProperty("status").GetString().ShouldBe("queued");
        (await PollAsync(client, first.GetProperty("jobId").GetGuid())).Body.GetProperty("status").GetString().ShouldBe("queued");
    }

    [Fact]
    public async Task The_download_link_expires_but_asking_again_gives_a_fresh_one_while_the_file_exists()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);
        var client = host.ClientFor(seeded.Library.OwnerId);
        var id = (await StartAsync(client)).GetProperty("jobId").GetGuid();
        await host.RunJobsAsync();
        var link = (await PollAsync(client, id)).Body.GetProperty("downloadUrl").GetString()!;

        host.Time.Advance(host.Settings.LinkLifetime - TimeSpan.FromSeconds(1));
        (await client.GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Time.Advance(TimeSpan.FromSeconds(2));
        (await client.GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var fresh = (await PollAsync(client, id)).Body.GetProperty("downloadUrl").GetString()!;
        (await client.GetAsync(fresh)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_cleanup_job_removes_the_file_after_24_hours_and_the_export_is_gone()
    {
        var seeded = await SeedAsync();
        await using var host = new Host(Api);
        var client = host.ClientFor(seeded.Library.OwnerId);
        var id = (await StartAsync(client)).GetProperty("jobId").GetGuid();
        await host.RunJobsAsync();
        Directory.GetFiles(host.Folder, "*.zip").Length.ShouldBe(1);

        host.Time.Advance(TimeSpan.FromHours(23));
        await host.RunCleanupAsync();
        Directory.GetFiles(host.Folder, "*.zip").Length.ShouldBe(1);

        host.Time.Advance(TimeSpan.FromHours(2));
        await host.RunCleanupAsync();

        Directory.GetFiles(host.Folder, "*.zip").ShouldBeEmpty();
        (await PollAsync(client, id)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_library_of_5000_items_is_read_in_bounded_batches()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        await using (var context = Db.CreateContext())
        {
            context.Items.AddRange(Enumerable.Range(0, 5000).Select(n => NewItem(library, releaseId, $"Item {n}")));
            await context.SaveChangesAsync();
        }

        var batches = new List<int>();
        await using var host = new Host(Api, settings => settings.BatchObserved = batches.Add);

        var files = await ExportAsync(host, library.OwnerId);

        ParseCsv(files["items.csv"]).Count.ShouldBe(5000);
        JsonDocument.Parse(files["items.json"]).RootElement.GetArrayLength().ShouldBe(5000);
        batches.Max().ShouldBeLessThanOrEqualTo(host.Settings.BatchSize);
        batches.Count.ShouldBeGreaterThanOrEqualTo(2 * (5000 / host.Settings.BatchSize)); // once for the CSV, once for the JSON
        batches.Sum().ShouldBe(10000);
    }

    [Fact]
    public async Task The_zip_has_no_emails_tokens_or_other_peoples_details()
    {
        var seeded = await SeedAsync();
        await using (var context = Db.CreateContext())
        {
            await context.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, u => u.Id.ToString() + "@secret.example"));
        }

        await using var host = new Host(Api);
        var files = await ExportAsync(host, seeded.Library.ViewerId);

        string.Join('\n', files.Values).ShouldNotContain("secret.example");
        string.Join('\n', files.Values).ShouldNotContain(seeded.Library.OwnerId.ToString());
        files.Keys.OrderBy(k => k).ShouldBe(["feature-prints.json", "items.csv", "items.json", "locations.json", "photos.json", "tags.json"]);
    }
}

internal static class TestLibraryExtensions
{
    public static TestLibrary Library(this TestLibrary library) => library;
}
