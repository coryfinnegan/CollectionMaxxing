using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Catalog;

public class CoverEndpointTests(ApiFixture api) : CatalogTest(api)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private void WriteBlob(string relativePath, byte[]? bytes = null)
    {
        var full = Path.Combine(CoversRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes ?? Png);
    }

    [Fact]
    public async Task A_global_cover_is_served_to_any_signed_in_user_with_the_right_headers()
    {
        WriteBlob("ab/global.png");
        var id = await CatalogSeed.AddAssetAsync(Db, "ab/global.png", "image/png", sha256: "feedbeef");
        var stranger = await TestData.CreateUserAsync(Db);

        var response = await ClientWith(stranger).GetAsync($"/media/covers/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.ETag!.Tag.ShouldBe("\"feedbeef\"");
        var cache = response.Headers.CacheControl!;
        cache.Public.ShouldBeTrue();
        cache.MaxAge!.Value.TotalDays.ShouldBeGreaterThan(30);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Png);
    }

    [Theory]
    [InlineData(LibraryRole.Viewer)]
    [InlineData(LibraryRole.Editor)]
    [InlineData(LibraryRole.Owner)]
    public async Task A_private_cover_is_served_to_every_role_of_its_library_with_private_caching(LibraryRole role)
    {
        var library = await CreateLibraryAsync();
        WriteBlob("cd/private.png");
        var id = await CatalogSeed.AddAssetAsync(Db, "cd/private.png", ownerLibraryId: library.Id);

        var response = await ClientWith(library.UserWithRole(role)).GetAsync($"/media/covers/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cache = response.Headers.CacheControl!;
        cache.Private.ShouldBeTrue();
        cache.Public.ShouldBeFalse();
        cache.MaxAge.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_private_cover_is_404_for_a_member_of_another_library_and_for_a_user_in_no_library()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        WriteBlob("cd/private.png");
        var id = await CatalogSeed.AddAssetAsync(Db, "cd/private.png", ownerLibraryId: mine.Id);

        (await ClientWith(theirs.OwnerId).GetAsync($"/media/covers/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ClientWith(theirs.OutsiderId).GetAsync($"/media/covers/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_asset_is_404_and_an_anonymous_caller_is_401()
    {
        var id = await CatalogSeed.AddAssetAsync(Db, "ab/x.png");

        (await ClientWith(await TestData.CreateUserAsync(Db)).GetAsync($"/media/covers/{Guid.NewGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ClientWith(null).GetAsync($"/media/covers/{id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_missing_blob_is_404_not_500()
    {
        var id = await CatalogSeed.AddAssetAsync(Db, "ab/not-there.png");

        var response = await ClientWith(await TestData.CreateUserAsync(Db)).GetAsync($"/media/covers/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("ab/../../secret.png")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("ab\\..\\secret.png")]
    public async Task A_traversal_style_blob_path_in_the_database_is_refused(string blobPath)
    {
        // A real file just outside the root, which a naive Path.Combine would happily serve.
        var outside = Path.Combine(Path.GetDirectoryName(CoversRoot)!, "secret.png");
        File.WriteAllBytes(outside, Png);
        try
        {
            var id = await CatalogSeed.AddAssetAsync(Db, blobPath);

            var response = await ClientWith(await TestData.CreateUserAsync(Db)).GetAsync($"/media/covers/{id}");

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task A_stored_type_that_is_not_an_image_is_not_served()
    {
        WriteBlob("ab/page.html", "<script>alert(1)</script>"u8.ToArray());
        var id = await CatalogSeed.AddAssetAsync(Db, "ab/page.html", "text/html");

        var response = await ClientWith(await TestData.CreateUserAsync(Db)).GetAsync($"/media/covers/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_repeat_request_with_the_etag_gets_304()
    {
        WriteBlob("ab/global.png");
        var id = await CatalogSeed.AddAssetAsync(Db, "ab/global.png", sha256: "feedbeef");
        var client = ClientWith(await TestData.CreateUserAsync(Db));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/covers/{id}");
        request.Headers.IfNoneMatch.ParseAdd("\"feedbeef\"");
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }
}
