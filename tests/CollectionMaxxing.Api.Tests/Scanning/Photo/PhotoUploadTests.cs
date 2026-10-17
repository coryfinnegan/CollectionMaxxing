using System.Net;
using CollectionMaxxing.Api.Tests.Scanning.Events;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Photo;

public class PhotoUploadTests : PhotoTest
{
    public PhotoUploadTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services => services.AddSingleton(new ScanEventOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            Heartbeat = TimeSpan.FromMilliseconds(250),
        });
    }

    [Fact]
    public async Task Uploading_a_photo_answers_with_the_candidate_and_stores_one_library_private_cover_asset()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, releaseId) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);

        var response = await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain());

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = await ReadJsonAsync(response);
        Id(candidate).ShouldBe(candidateId);
        candidate.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(releaseId);
        var asset = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();
        asset.OwnerLibraryId.ShouldBe(library.Id);
        asset.Kind.ShouldBe(Domain.Catalog.AssetKind.UserPhoto);
        asset.BlobPath.ShouldNotContain("..");
        StoredFileExists(asset).ShouldBeTrue();
        (await AssetsOfAsync(library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_stored_name_is_ours_and_never_the_name_the_client_sent()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);

        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain(), fileName: "../../evil name.php")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var asset = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();
        asset.BlobPath.ShouldNotContain("evil");
        asset.BlobPath.ShouldEndWith(".jpg");
        FilesOnDisk().ShouldHaveSingleItem().ShouldStartWith(PhotosRoot);
    }

    [Fact]
    public async Task After_commit_the_item_cover_is_the_photo_and_the_other_candidates_keep_the_catalogue_cover()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, withPhoto, _) = await ScanOneAsync(library);
        var withoutPhoto = await ScanAnotherAsync(library, sessionId, 2);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, sessionId, withPhoto, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var asset = (await PhotoOfAsync(withPhoto)).ShouldNotBeNull();

        var commit = await CommitAsync(editor, sessionId);

        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(2);
        var photoItemId = (await CandidateAsync(withPhoto)).CreatedItemId;
        var plainItemId = (await CandidateAsync(withoutPhoto)).CreatedItemId;
        var photoItem = items.Single(i => i.Id == photoItemId);
        photoItem.CoverChoice.ShouldBe(CoverChoice.MyPhoto);
        photoItem.CustomCoverAssetId.ShouldBe(asset.Id);
        var plainItem = items.Single(i => i.Id == plainItemId);
        plainItem.CoverChoice.ShouldBe(CoverChoice.Release);
        plainItem.CustomCoverAssetId.ShouldBeNull();
        StoredFileExists(asset).ShouldBeTrue("the committed item still needs its cover");

        // The item list shows the photo as the cover.
        var list = await ReadJsonAsync(await editor.GetAsync($"/api/v1/libraries/{library.Id}/items"));
        var listed = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == photoItem.Id);
        listed.GetProperty("coverUrl").GetString()!.ShouldContain(asset.Id.ToString());
    }

    [Fact]
    public async Task Every_copy_of_an_accepted_candidate_gets_the_photo()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);
        (await DecideAsync(editor, sessionId, candidateId, new { copies = 2 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var asset = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();

        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.CoverChoice == CoverChoice.MyPhoto && i.CustomCoverAssetId == asset.Id);
    }

    [Fact]
    public async Task A_rejected_candidates_photo_is_removed_at_commit_and_the_accepted_ones_stays()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, kept, _) = await ScanOneAsync(library);
        var rejected = await ScanAnotherAsync(library, sessionId, 2);
        var undecided = await ScanAnotherAsync(library, sessionId, 3);
        using var editor = As(library, LibraryRole.Editor);
        foreach (var candidate in new[] { kept, rejected, undecided })
        {
            (await UploadAsync(editor, sessionId, candidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await DecideAsync(editor, sessionId, rejected, new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync(editor, sessionId, undecided, new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var keptAsset = (await PhotoOfAsync(kept)).ShouldNotBeNull();

        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await AssetsOfAsync(library.Id)).ShouldHaveSingleItem().Id.ShouldBe(keptAsset.Id);
        FilesOnDisk().Length.ShouldBe(1);
        (await PhotoOfAsync(rejected)).ShouldBeNull();
        await using var context = Db.CreateContext();
        (await context.ScanSnaps.IgnoreQueryFilters().CountAsync(s => s.SessionId == sessionId && s.AssetId != null)).ShouldBe(1);
    }

    [Fact]
    public async Task An_abandoned_session_leaves_no_photo_behind_once_the_clean_up_job_has_run()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await editor.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var context = Db.CreateContext())
        {
            await context.ScanSessions.IgnoreQueryFilters().Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-8)));
        }

        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CleanUpScanSessions>().HandleAsync(new CleanUpScanSessions.Job(), CancellationToken.None);
        }

        (await AssetsOfAsync(library.Id)).ShouldBeEmpty();
        FilesOnDisk().ShouldBeEmpty();
        await using var check = Db.CreateContext();
        (await check.ScanSessions.IgnoreQueryFilters().AnyAsync(s => s.Id == sessionId)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_open_session_that_went_quiet_is_cleaned_up_with_its_photos_and_a_committed_ones_cover_is_left_alone()
    {
        var library = await CreateLibraryAsync();
        var (quiet, quietCandidate, _) = await ScanOneAsync(library);
        var (done, doneCandidate, _) = await ScanOneAsync(library, number: 2);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, quiet, quietCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UploadAsync(editor, done, doneCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var cover = (await PhotoOfAsync(doneCandidate)).ShouldNotBeNull();
        (await CommitAsync(editor, done)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var context = Db.CreateContext())
        {
            await context.ScanSessions.IgnoreQueryFilters().Where(s => s.Id == quiet || s.Id == done)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-40)));
        }

        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CleanUpScanSessions>().HandleAsync(new CleanUpScanSessions.Job(), CancellationToken.None);
        }

        (await AssetsOfAsync(library.Id)).ShouldHaveSingleItem().Id.ShouldBe(cover.Id);
        FilesOnDisk().Length.ShouldBe(1);
        StoredFileExists(cover).ShouldBeTrue();
    }

    [Fact]
    public async Task Uploading_again_replaces_the_earlier_photo_without_leaving_it_behind()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();

        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.WithSize(16, 16))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();
        second.Id.ShouldNotBe(first.Id);
        (await AssetsOfAsync(library.Id)).ShouldHaveSingleItem().Id.ShouldBe(second.Id);
        FilesOnDisk().Length.ShouldBe(1);
        StoredFileExists(first).ShouldBeFalse();
    }

    [Fact]
    public async Task Uploading_keeps_the_candidates_other_state_and_the_photo_of_one_candidate_is_not_shared_with_another()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, first, _) = await ScanOneAsync(library);
        var second = await ScanAnotherAsync(library, sessionId, 2);
        using var editor = As(library, LibraryRole.Editor);
        (await DecideAsync(editor, sessionId, first, new { copies = 3 })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await UploadAsync(editor, sessionId, first, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);

        var candidate = await CandidateAsync(first);
        candidate.Copies.ShouldBe(3);
        candidate.Decision.ShouldBe(CandidateDecision.Accepted);
        (await PhotoOfAsync(second)).ShouldBeNull();
    }

    [Fact]
    public async Task A_photo_for_a_session_that_is_already_committed_or_abandoned_is_a_conflict_and_stores_nothing()
    {
        var library = await CreateLibraryAsync();
        var (committed, committedCandidate, _) = await ScanOneAsync(library);
        var (abandoned, abandonedCandidate, _) = await ScanOneAsync(library, number: 2);
        using var editor = As(library, LibraryRole.Editor);
        (await CommitAsync(editor, committed)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await editor.DeleteAsync(SessionUrl(abandoned))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await UploadAsync(editor, committed, committedCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await UploadAsync(editor, abandoned, abandonedCandidate, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await AssetsOfAsync(library.Id)).ShouldBeEmpty();
        FilesOnDisk().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_event_stream_says_the_candidate_was_updated_and_the_photo_is_ready()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);
        await using var events = await SseClient.ConnectAsync(editor, SessionUrl(sessionId) + "/events");
        (await events.ReadUntilAsync("candidate.added")).ShouldNotBeEmpty();

        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = await events.ReadUntilAsync("photo.status");
        read.Select(e => e.Name).ShouldContain("candidate.updated");
        var status = read.Last().Data!.Value;
        status.GetProperty("candidateId").GetGuid().ShouldBe(candidateId);
        status.GetProperty("status").GetString().ShouldBe("ready");
    }

    // ---- what is refused ------------------------------------------------------------------------------------------------

    public static IEnumerable<TheoryDataRow<string, byte[], string, string>> Refused() =>
    [
        new("a PNG renamed .jpg", JpegFixtures.Png(), "crop.jpg", "image/jpeg"),
        new("a PNG claiming to be a JPEG by type only", JpegFixtures.Png(), "crop.png", "image/jpeg"),
        new("an HTML file", JpegFixtures.Html(), "crop.jpg", "image/jpeg"),
        new("an HTML file named like a picture", JpegFixtures.Html(), "x.jpg", "text/html"),
        new("a truncated JPEG", JpegFixtures.TruncatedInScan(), "crop.jpg", "image/jpeg"),
        new("a JPEG cut off in its header", JpegFixtures.TruncatedInHeader(), "crop.jpg", "image/jpeg"),
        new("an empty file", [], "crop.jpg", "image/jpeg"),
        new("a JPEG with an enormous frame", JpegFixtures.WithSize(30000, 30000), "crop.jpg", "image/jpeg"),
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_file_that_is_not_a_sound_jpeg_is_refused_with_a_problem_and_nothing_is_stored(string what, byte[] bytes, string fileName, string contentType)
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);

        var response = await UploadAsync(editor, sessionId, candidateId, bytes, fileName, contentType);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, what);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await AssetsOfAsync(library.Id)).ShouldBeEmpty();
        FilesOnDisk().ShouldBeEmpty();
        (await PhotoOfAsync(candidateId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_file_over_four_megabytes_is_refused_with_413_and_nothing_is_stored()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);

        var response = await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Oversize((4 * 1024 * 1024) + 1));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await AssetsOfAsync(library.Id)).ShouldBeEmpty();
        FilesOnDisk().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_of_exactly_four_megabytes_is_not_refused_for_its_size()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);

        var response = await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Oversize(4 * 1024 * 1024));

        // Padding after the end marker is not part of the picture, so it is dropped; the size rule alone does not refuse it.
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        StoredBytes((await PhotoOfAsync(candidateId)).ShouldNotBeNull()).ShouldBe(JpegFixtures.Plain());
    }

    [Fact]
    public async Task A_request_that_is_not_a_multipart_upload_or_has_no_file_is_refused()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);

        var json = await editor.PostAsync(PhotoUrl(sessionId, candidateId), Json(new { file = "abc" }));
        var wrongPart = await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain(), part: "picture");
        using var empty = new MultipartFormDataContent();
        var noParts = await editor.PostAsync(PhotoUrl(sessionId, candidateId), empty);

        json.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        wrongPart.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await wrongPart.Content.ReadAsStringAsync());
        noParts.StatusCode.ShouldBe(HttpStatusCode.BadRequest, string.Join("\n", Logs.Lines.Where(l => l.Contains("Exception", StringComparison.Ordinal)).Take(3)));
        foreach (var response in new[] { json, wrongPart, noParts })
        {
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        }

        (await AssetsOfAsync(library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_second_upload_keeps_the_first_photo()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = (await PhotoOfAsync(candidateId)).ShouldNotBeNull();

        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Html())).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await PhotoOfAsync(candidateId)).ShouldNotBeNull().Id.ShouldBe(first.Id);
        StoredFileExists(first).ShouldBeTrue();
    }
}
