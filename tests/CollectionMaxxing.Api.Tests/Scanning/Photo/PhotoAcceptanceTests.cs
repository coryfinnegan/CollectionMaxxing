using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Photo;

// LOCKED acceptance tests of SCAN-2c (issue #114): the cover photo upload and the feature-print backup. Do not edit, weaken, skip or
// delete: add new tests instead.
public class PhotoAcceptanceTests : PhotoTest
{
    private readonly string _exports = Path.Combine(Path.GetTempPath(), "cm-photo-exports-" + Guid.NewGuid().ToString("N"));

    public PhotoAcceptanceTests(ApiFixture api)
        : base(api)
    {
        Settings["Exports:Path"] = _exports;
    }

    public override ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_exports))
            {
                Directory.Delete(_exports, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }

        GC.SuppressFinalize(this);
        return base.DisposeAsync();
    }

    private static string Base64(params byte[] data) => Convert.ToBase64String(data);

    private static StringContent Prints(params (Guid ItemId, Guid ReleaseId, int Revision, string Data)[] prints) => Json(new
    {
        prints = prints.Select(p => new { itemId = p.ItemId, releaseId = p.ReleaseId, revision = p.Revision, data = p.Data, createdAt = DateTimeOffset.UtcNow }),
    });

    [Fact]
    public async Task A_viewer_cannot_upload_a_photo_but_can_read_feature_prints_while_an_editor_can_do_both_and_anonymous_gets_401()
    {
        var library = await CreateLibraryAsync();
        var (viewerSession, viewerCandidate, _) = await ScanOneAsync(library, LibraryRole.Viewer);
        var (editorSession, editorCandidate, _) = await ScanOneAsync(library, LibraryRole.Editor, number: 2);
        var itemId = await TestData.CreateItemAsync(Db, library);
        var releaseId = await TestData.CreateReleaseAsync(Db);

        using var viewer = As(library, LibraryRole.Viewer);
        using var editor = As(library, LibraryRole.Editor);
        using var anonymous = Factory.CreateClient();

        (await UploadAsync(viewer, viewerSession, viewerCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.GetAsync(FeaturePrintsUrl(library.Id))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.PutAsync(FeaturePrintsUrl(library.Id), Prints((itemId, releaseId, 1, Base64(1, 2, 3))))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await UploadAsync(editor, editorSession, editorCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await editor.PutAsync(FeaturePrintsUrl(library.Id), Prints((itemId, releaseId, 1, Base64(1, 2, 3))))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await editor.GetAsync(FeaturePrintsUrl(library.Id))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await UploadAsync(anonymous, editorSession, editorCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(FeaturePrintsUrl(library.Id))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsync(FeaturePrintsUrl(library.Id), Prints((itemId, releaseId, 2, Base64(1))))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await AssetsOfAsync(library.Id)).Count.ShouldBe(1, "only the editor's photo was stored");
    }

    [Fact]
    public async Task Another_librarys_session_candidate_item_or_release_is_404_on_all_three_routes_and_nothing_leaks_or_is_written()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var (theirSession, theirCandidate, _) = await ScanOneAsync(theirs);
        var (mySession, myCandidate, _) = await ScanOneAsync(mine, number: 2);
        var theirItem = await TestData.CreateItemAsync(Db, theirs);
        var myItem = await TestData.CreateItemAsync(Db, mine);
        var theirPrivateRelease = await TestData.CreateReleaseAsync(Db, theirs.Id);
        var sharedRelease = await TestData.CreateReleaseAsync(Db);
        using var me = As(mine, LibraryRole.Owner);
        using var they = As(theirs, LibraryRole.Owner);

        // The photo route: their session, their candidate in my session's url, my candidate in their session's url, and an id nobody has.
        (await UploadAsync(me, theirSession, theirCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UploadAsync(me, mySession, theirCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UploadAsync(me, theirSession, myCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UploadAsync(me, mySession, Guid.NewGuid(), JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AssetsOfAsync(theirs.Id)).ShouldBeEmpty();
        (await AssetsOfAsync(mine.Id)).ShouldBeEmpty();
        FilesOnDisk().ShouldBeEmpty();

        // Writing prints: their item, their library's private release, and an id nobody has, through my library: all 404, all or nothing.
        (await me.PutAsync(FeaturePrintsUrl(mine.Id), Prints((theirItem, sharedRelease, 1, Base64(1))))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.PutAsync(FeaturePrintsUrl(mine.Id), Prints((myItem, theirPrivateRelease, 1, Base64(1))))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.PutAsync(FeaturePrintsUrl(mine.Id), Prints((myItem, sharedRelease, 1, Base64(2)), (Guid.NewGuid(), sharedRelease, 1, Base64(3)))))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.PutAsync(FeaturePrintsUrl(theirs.Id), Prints((theirItem, sharedRelease, 1, Base64(1))))).StatusCode.ShouldBe(HttpStatusCode.NotFound); // not my library
        await using (var context = Db.CreateContext())
        {
            (await context.ItemFeaturePrints.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        }

        // Reading prints: another library's route is 404, and my route never shows theirs.
        (await they.PutAsync(FeaturePrintsUrl(theirs.Id), Prints((theirItem, sharedRelease, 1, Base64(9))))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await me.GetAsync(FeaturePrintsUrl(theirs.Id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var mine404 = await me.GetAsync(FeaturePrintsUrl(mine.Id));
        mine404.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(mine404)).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_stored_photo_is_readable_only_by_members_of_its_library()
    {
        var library = await CreateLibraryAsync();
        var elsewhere = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.WithPhoneMetadata())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var asset = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();

        foreach (var role in new[] { LibraryRole.Viewer, LibraryRole.Editor, LibraryRole.Owner })
        {
            using var member = As(library, role);
            var response = await member.GetAsync($"/media/covers/{asset.Id}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, role.ToString());
            response.Headers.CacheControl!.Private.ShouldBeTrue();
            (await response.Content.ReadAsByteArrayAsync()).ShouldBe(StoredBytes(asset));
        }

        using var otherMember = As(elsewhere, LibraryRole.Owner);
        (await otherMember.GetAsync($"/media/covers/{asset.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var nobody = AsUser(elsewhere.OutsiderId);
        (await nobody.GetAsync($"/media/covers/{asset.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var anonymous = Factory.CreateClient();
        (await anonymous.GetAsync($"/media/covers/{asset.Id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_uploaded_photo_has_no_exif_or_gps_after_storage_and_the_original_bytes_are_not_kept()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        var original = JpegFixtures.WithPhoneMetadata();
        JpegFixtures.Contains(original, JpegFixtures.GpsText).ShouldBeTrue();
        using var editor = As(library, LibraryRole.Editor);

        (await UploadAsync(editor, sessionId, candidateId, original)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var asset = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();
        var stored = StoredBytes(asset);
        foreach (var secret in new[] { "Exif", "GPS", JpegFixtures.GpsText, JpegFixtures.IccText, JpegFixtures.PhotoshopText, JpegFixtures.CommentText, JpegFixtures.TrailingText })
        {
            JpegFixtures.Contains(stored, secret).ShouldBeFalse($"stored photo must not contain '{secret}'");
        }

        stored.ShouldBe(JpegFixtures.Plain()); // still the same picture: only the metadata went
        asset.ContentType.ShouldBe("image/jpeg");
        (asset.Width, asset.Height).ShouldBe((8, 8));
        asset.Sha256.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stored)));

        // The original upload is nowhere on disk, and the only file is the cleaned one.
        var files = FilesOnDisk();
        files.Length.ShouldBe(1);
        foreach (var file in files)
        {
            File.ReadAllBytes(file).AsSpan().IndexOf(Encoding.ASCII.GetBytes("GPSLatitude")).ShouldBe(-1);
        }

        // ... and what the server sends back has none of it either.
        using var member = As(library, LibraryRole.Viewer);
        var served = await (await member.GetAsync($"/media/covers/{asset.Id}")).Content.ReadAsByteArrayAsync();
        JpegFixtures.Contains(served, "GPS").ShouldBeFalse();
    }

    [Fact]
    public async Task The_purge_removes_the_users_photos_and_feature_prints_and_the_export_lists_them_and_never_another_users()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var (mySession, myCandidate, _) = await ScanOneAsync(mine);
        var (theirSession, theirCandidate, _) = await ScanOneAsync(theirs, number: 2);
        var myItem = await TestData.CreateItemAsync(Db, mine);
        var theirItem = await TestData.CreateItemAsync(Db, theirs);
        var release = await TestData.CreateReleaseAsync(Db);
        using var me = As(mine, LibraryRole.Owner);
        using var they = As(theirs, LibraryRole.Owner);
        (await UploadAsync(me, mySession, myCandidate, JpegFixtures.WithPhoneMetadata())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UploadAsync(they, theirSession, theirCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await me.PutAsync(FeaturePrintsUrl(mine.Id), Prints((myItem, release, 1, Base64(1, 2, 3))))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await they.PutAsync(FeaturePrintsUrl(theirs.Id), Prints((theirItem, release, 1, Base64(4, 5, 6))))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var myPhoto = (await PhotoOfAsync(myCandidate)).ShouldNotBeNull();
        var theirPhoto = (await PhotoOfAsync(theirCandidate)).ShouldNotBeNull();

        // The export lists my photo and my feature print: not theirs.
        var export = await ExportAsync(mine.OwnerId);
        var photos = JsonDocument.Parse(export["photos.json"]).RootElement.EnumerateArray().ToList();
        photos.Select(p => p.GetProperty("assetId").GetGuid()).ShouldBe([myPhoto.Id]);
        photos[0].GetProperty("libraryId").GetGuid().ShouldBe(mine.Id);
        var prints = JsonDocument.Parse(export["feature-prints.json"]).RootElement.EnumerateArray().ToList();
        prints.Select(p => p.GetProperty("itemId").GetGuid()).ShouldBe([myItem]);
        export["photos.json"].ShouldNotContain(theirPhoto.Id.ToString());
        export["feature-prints.json"].ShouldNotContain(theirItem.ToString());
        export.Values.ShouldAllBe(text => !text.Contains("GPS", StringComparison.Ordinal));

        // Deleting my account, 30 days on: my photo and prints go with the library; theirs stay.
        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.LibraryId == mine.Id && m.Role != LibraryRole.Owner).ExecuteDeleteAsync();
            await context.Users.Where(u => u.Id == mine.OwnerId).ExecuteUpdateAsync(set => set.SetProperty(u => u.DeletedAt, DateTimeOffset.UtcNow.AddDays(-31)));
        }

        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(
                PurgeAccount.JobType, new PurgeAccountPayload(mine.OwnerId), new JobEnqueueOptions(null, PurgeAccount.DedupeKey(mine.OwnerId)), CancellationToken.None);
        }

        var runner = Factory.Services.GetRequiredService<JobRunner>();
        while (await runner.RunNextAsync("test-purge", CancellationToken.None))
        {
        }

        (await AssetsOfAsync(mine.Id)).ShouldBeEmpty();
        StoredFileExists(myPhoto).ShouldBeFalse("the file is deleted, not just the row");
        await using var check = Db.CreateContext();
        (await check.ItemFeaturePrints.IgnoreQueryFilters().Where(p => p.LibraryId == mine.Id).CountAsync()).ShouldBe(0);
        (await AssetsOfAsync(theirs.Id)).ShouldHaveSingleItem().Id.ShouldBe(theirPhoto.Id);
        StoredFileExists(theirPhoto).ShouldBeTrue();
        (await check.ItemFeaturePrints.IgnoreQueryFilters().Where(p => p.LibraryId == theirs.Id).CountAsync()).ShouldBe(1);
    }

    /// <summary>Starts the person's export, runs the jobs and unzips what they would download.</summary>
    private async Task<Dictionary<string, string>> ExportAsync(Guid userId)
    {
        using var client = AsUser(userId);
        var started = await client.GetAsync("/api/v1/me/export");
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var jobId = (await ReadJsonAsync(started)).GetProperty("jobId").GetGuid();

        var runner = Factory.Services.GetRequiredService<JobRunner>();
        while (await runner.RunNextAsync("test-export", CancellationToken.None))
        {
        }

        var status = await ReadJsonAsync(await client.GetAsync($"/api/v1/me/export/{jobId}"));
        status.GetProperty("status").GetString().ShouldBe("ready");
        var download = await client.GetAsync(status.GetProperty("downloadUrl").GetString());
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var zip = new ZipArchive(new MemoryStream(await download.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }
}
