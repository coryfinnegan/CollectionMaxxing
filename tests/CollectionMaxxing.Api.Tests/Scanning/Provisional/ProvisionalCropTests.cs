using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

/// <summary>The segment-crop upload (#345, ADR-030): kept T2 rows may send their crop to the household API, successes never do.</summary>
public class ProvisionalCropTests : ProvisionalTest
{
    public ProvisionalCropTests(ApiFixture api)
        : base(api)
    {
    }

    [Fact]
    public async Task Uploading_a_crop_for_an_unresolved_row_stores_a_sanitized_scan_photo()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);

        var response = await UploadCropAsync(editor, sessionId, candidateId, JpegFixtures.WithPhoneMetadata());

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        var assetId = body.GetProperty("assetId").GetGuid();
        body.GetProperty("cropUrl").GetString().ShouldBe($"/media/covers/{assetId}");
        var asset = (await AssetAsync(assetId)).ShouldNotBeNull();
        asset.Kind.ShouldBe(AssetKind.ScanPhoto);
        asset.OwnerLibraryId.ShouldBe(library.Id);
        asset.BlobPath.ShouldStartWith("crops/");
        StoredFileExists(asset).ShouldBeTrue();
        var stored = StoredBytes(asset);
        JpegFixtures.Contains(stored, JpegFixtures.GpsText).ShouldBeFalse();
        JpegFixtures.Contains(stored, JpegFixtures.CommentText).ShouldBeFalse();
    }

    [Fact]
    public async Task A_crop_over_1mb_is_refused_and_nothing_is_stored()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);

        var response = await UploadCropAsync(editor, sessionId, candidateId, JpegFixtures.Oversize(1024 * 1024 + 1));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        FilesOnDisk().ShouldBeEmpty();
        (await ProvisionalsOfAsync(library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_crop_that_is_not_a_jpeg_is_refused()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);

        var response = await UploadCropAsync(editor, sessionId, candidateId, JpegFixtures.Png());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        FilesOnDisk().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_crop_for_an_accepted_candidate_is_refused_successes_never_upload()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9101);
        await CatalogAsync("Crop success guard", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id); // crop guard, not auto-add (#341)
        var candidateId = Id((await ScanAsync(editor, sessionId, code))[0]);
        (await DecideAsync(editor, sessionId, candidateId, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await UploadCropAsync(editor, sessionId, candidateId, JpegFixtures.Plain());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        FilesOnDisk().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_crop_for_a_candidate_from_another_session_is_not_found()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var otherId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, otherId))[0]);

        var response = await UploadCropAsync(editor, sessionId, candidateId, JpegFixtures.Plain());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        FilesOnDisk().ShouldBeEmpty();
    }
}
