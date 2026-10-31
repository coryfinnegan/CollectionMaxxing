using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning.Artwork;

/// <summary>
/// <c>POST …/candidates/{id}:verify-cover</c> (#438 stage 2), end to end with the real SIFT verifier: the Suddenly You
/// tie (identical text scores for the 2001 and 2003 editions) breaks toward the edition whose cover matches the capture.
/// </summary>
public class CoverVerifyTests : ArtworkTest
{
    /// <summary>The most bytes of quad JSON the endpoint reads.</summary>
    private const int QuadLimit = 1024;

    public CoverVerifyTests(ApiFixture api)
        : base(api)
    {
        Settings["Catalog:CoversRoot"] = CoversDir;
    }

    private string CoversDir { get; } = Path.Combine(Path.GetTempPath(), "cm-verify-" + Guid.NewGuid().ToString("N"));

    public override ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(CoversDir))
            {
                Directory.Delete(CoversDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }

        GC.SuppressFinalize(this);
        return base.DisposeAsync();
    }

    private void WriteBlob(string relativePath, byte[] bytes)
    {
        var full = Path.Combine(CoversDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>The Suddenly You tie: two editions, identical text scores, each with its real cover art on disk.</summary>
    private async Task<(Guid Release2001, Guid Release2003)> SeedEditionsAsync(byte[] cover2001, byte[] cover2003)
    {
        WriteBlob("sy/2001.png", cover2001);
        WriteBlob("sy/2003.png", cover2003);
        var asset2001 = await CatalogSeed.AddAssetAsync(Db, "sy/2001.png", "image/png");
        var asset2003 = await CatalogSeed.AddAssetAsync(Db, "sy/2003.png", "image/png");
        var (_, release2001) = await CatalogSeed.AddAsync(
            Db, "Suddenly You", WorkKind.Book, 2001, coverAssetId: asset2001, format: ReleaseFormat.Paperback);
        var (_, release2003) = await CatalogSeed.AddAsync(
            Db, "Suddenly You", WorkKind.Book, 2003, coverAssetId: asset2003, format: ReleaseFormat.Paperback);
        return (release2001, release2003);
    }

    private static string QuadJson(CoverFixtures.CaptureCorners corners) =>
        JsonSerializer.Serialize(new
        {
            topLeft = Point(corners.TopLeft),
            topRight = Point(corners.TopRight),
            bottomRight = Point(corners.BottomRight),
            bottomLeft = Point(corners.BottomLeft),
        });

    private static object Point(OpenCvSharp.Point2f corner) => new
    {
        x = corner.X / CoverFixtures.CaptureWidth,
        y = corner.Y / CoverFixtures.CaptureHeight,
    };

    private static async Task<HttpResponseMessage> PostCaptureAsync(
        HttpClient client, Guid sessionId, Guid candidateId, byte[] capture, string? quadJson)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(capture);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "capture.jpg");
        if (quadJson is not null)
        {
            form.Add(new StringContent(quadJson), "quad");
        }

        return await client.PostAsync($"{SessionUrl(sessionId)}/candidates/{candidateId}:verify-cover", form);
    }

    [Fact]
    public async Task A_capture_of_the_2001_cover_preselects_the_2001_edition()
    {
        using var edition2001 = CoverFixtures.Edition2001();
        using var edition2003 = CoverFixtures.Edition2003();
        var corners = CoverFixtures.GentleCorners();
        var capture = CoverFixtures.CaptureOf(edition2001, corners);
        var (release2001, _) = await SeedEditionsAsync(CoverFixtures.PngOf(edition2001), CoverFixtures.PngOf(edition2003));
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await IdentifyOkAsync(
            editor, sessionId, Read(title: "Suddenly You", kind: "book", creator: "Lisa Kleypas")))[0];
        Resolution(candidate).ShouldBe("ambiguous");

        var verified = await ReadOkAsync(await PostCaptureAsync(editor, sessionId, Id(candidate), capture, QuadJson(corners)));

        AlternativeIds(verified)[0].ShouldBe(release2001);
        var pick = verified.GetProperty("coverPick");
        pick.GetProperty("releaseId").GetGuid().ShouldBe(release2001);
        pick.GetProperty("reason").GetString().ShouldBe("cover_match");
    }

    [Fact]
    public async Task Without_a_quad_the_same_capture_still_preselects()
    {
        using var edition2001 = CoverFixtures.Edition2001();
        using var edition2003 = CoverFixtures.Edition2003();
        var capture = CoverFixtures.CaptureOf(edition2001, CoverFixtures.GentleCorners());
        var (release2001, _) = await SeedEditionsAsync(CoverFixtures.PngOf(edition2001), CoverFixtures.PngOf(edition2003));
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await IdentifyOkAsync(
            editor, sessionId, Read(title: "Suddenly You", kind: "book", creator: "Lisa Kleypas")))[0];

        var verified = await ReadOkAsync(await PostCaptureAsync(editor, sessionId, Id(candidate), capture, null));

        verified.GetProperty("coverPick").GetProperty("releaseId").GetGuid().ShouldBe(release2001);
    }

    [Fact]
    public async Task After_an_indecisive_art_post_the_capture_still_verifies()
    {
        // #486: the on-device comparison runs but cannot tell the editions apart (equal distances keep the tie), so
        // the :art answer comes back needsArt=false with needsCoverVerify=true — and the capture escalates to the
        // server's own SIFT comparison instead of being refused.
        using var edition2001 = CoverFixtures.Edition2001();
        using var edition2003 = CoverFixtures.Edition2003();
        var corners = CoverFixtures.GentleCorners();
        var capture = CoverFixtures.CaptureOf(edition2001, corners);
        var (release2001, _) = await SeedEditionsAsync(CoverFixtures.PngOf(edition2001), CoverFixtures.PngOf(edition2003));
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await IdentifyOkAsync(
            editor, sessionId, Read(title: "Suddenly You", kind: "book", creator: "Lisa Kleypas")))[0];
        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("needsCoverVerify").GetBoolean().ShouldBeTrue();

        var ids = AlternativeIds(candidate);
        var afterArt = await ReadOkAsync(await editor.PostAsync(
            ArtUrl(sessionId, Id(candidate)), Json(new { distances = new[] { Distance(ids[0], 0.1), Distance(ids[1], 0.1) } })));
        Resolution(afterArt).ShouldBe("ambiguous");
        Decision(afterArt).ShouldBe("undecided");
        afterArt.GetProperty("needsArt").GetBoolean().ShouldBeFalse();
        afterArt.GetProperty("needsCoverVerify").GetBoolean().ShouldBeTrue();

        var verified = await ReadOkAsync(await PostCaptureAsync(editor, sessionId, Id(candidate), capture, QuadJson(corners)));

        AlternativeIds(verified)[0].ShouldBe(release2001);
        verified.GetProperty("coverPick").GetProperty("releaseId").GetGuid().ShouldBe(release2001);
    }

    [Fact]
    public async Task A_non_jpeg_capture_is_refused()
    {
        using var edition2001 = CoverFixtures.Edition2001();
        using var edition2003 = CoverFixtures.Edition2003();
        await SeedEditionsAsync(CoverFixtures.PngOf(edition2001), CoverFixtures.PngOf(edition2003));
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await IdentifyOkAsync(
            editor, sessionId, Read(title: "Suddenly You", kind: "book", creator: "Lisa Kleypas")))[0];

        var response = await PostCaptureAsync(editor, sessionId, Id(candidate), [1, 2, 3], null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.photo_not_jpeg");
    }

    [Fact]
    public async Task A_settled_row_is_not_verified()
    {
        using var only = CoverFixtures.Edition2001();
        WriteBlob("sy/only.png", CoverFixtures.PngOf(only));
        var assetId = await CatalogSeed.AddAssetAsync(Db, "sy/only.png", "image/png");
        await CatalogSeed.AddAsync(Db, "Suddenly You", WorkKind.Book, 2001, coverAssetId: assetId, format: ReleaseFormat.Paperback);
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await IdentifyOkAsync(
            editor, sessionId, Read(title: "Suddenly You", kind: "book", creator: "Lisa Kleypas")))[0];
        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("needsCoverVerify").GetBoolean().ShouldBeFalse();
        using var edition = CoverFixtures.Edition2001();
        var capture = CoverFixtures.CaptureOf(edition, CoverFixtures.GentleCorners());

        var response = await PostCaptureAsync(editor, sessionId, Id(candidate), capture, null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.cover_not_needed");
    }

    [Fact]
    public async Task A_missing_file_part_is_refused()
    {
        using var edition2001 = CoverFixtures.Edition2001();
        using var edition2003 = CoverFixtures.Edition2003();
        await SeedEditionsAsync(CoverFixtures.PngOf(edition2001), CoverFixtures.PngOf(edition2003));
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await IdentifyOkAsync(
            editor, sessionId, Read(title: "Suddenly You", kind: "book", creator: "Lisa Kleypas")))[0];
        using var form = new MultipartFormDataContent { { new StringContent("{}"), "quad" } };

        var response = await editor.PostAsync($"{SessionUrl(sessionId)}/candidates/{Id(candidate)}:verify-cover", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.cover_no_file");
    }

    /// <summary>A session with one ambiguous row, which is what a capture is verified against.</summary>
    private async Task<(HttpClient Editor, Guid SessionId, Guid CandidateId)> AmbiguousRowAsync()
    {
        using var edition2001 = CoverFixtures.Edition2001();
        using var edition2003 = CoverFixtures.Edition2003();
        await SeedEditionsAsync(CoverFixtures.PngOf(edition2001), CoverFixtures.PngOf(edition2003));
        var library = await CreateLibraryAsync();
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await IdentifyOkAsync(
            editor, sessionId, Read(title: "Suddenly You", kind: "book", creator: "Lisa Kleypas")))[0];
        return (editor, sessionId, Id(candidate));
    }

    [Fact]
    public async Task A_capture_over_four_megabytes_is_refused_with_413()
    {
        var (editor, sessionId, candidateId) = await AmbiguousRowAsync();
        using var _ = editor;

        var response = await PostCaptureAsync(editor, sessionId, candidateId, new byte[JpegSanitizer.MaxBytes + 1], null);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.cover_too_large");
    }

    [Fact]
    public async Task A_capture_of_exactly_four_megabytes_is_not_refused_for_its_size()
    {
        var (editor, sessionId, candidateId) = await AmbiguousRowAsync();
        using var _ = editor;

        var response = await PostCaptureAsync(editor, sessionId, candidateId, new byte[JpegSanitizer.MaxBytes], null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.photo_not_jpeg");
    }

    [Fact]
    public async Task A_quad_over_1024_bytes_is_refused_as_an_invalid_quad()
    {
        var (editor, sessionId, candidateId) = await AmbiguousRowAsync();
        using var _ = editor;
        var quad = QuadJson(CoverFixtures.GentleCorners()).PadRight(QuadLimit + 1);

        var response = await PostCaptureAsync(editor, sessionId, candidateId, [1, 2, 3], quad);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.cover_quad_invalid");
    }

    [Fact]
    public async Task A_quad_of_exactly_1024_bytes_is_read()
    {
        var (editor, sessionId, candidateId) = await AmbiguousRowAsync();
        using var _ = editor;
        var quad = QuadJson(CoverFixtures.GentleCorners()).PadRight(QuadLimit);

        var response = await PostCaptureAsync(editor, sessionId, candidateId, [1, 2, 3], quad);

        // The quad was accepted: what refuses the request is the capture, which is not a picture.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.photo_not_jpeg");
    }
}
