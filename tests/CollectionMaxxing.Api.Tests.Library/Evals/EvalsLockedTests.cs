using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// LOCKED acceptance tests for EVAL-1a (issue #122). The rules here protect the owner's private photos and the admin route that
/// accepts uploads. Never weaken, skip or delete them; change the code instead.
/// </summary>
public class EvalsLockedTests(ApiFixture api) : EvalTest(api)
{
    private static readonly SyntheticGolden.SingleItem CartTruth =
        new("Secret Cart Title", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    [Fact]
    public async Task Only_the_admin_can_reach_any_eval_route()
    {
        Golden.AddSingle("c1", CartTruth);
        var library = await CreateLibraryAsync();
        var otherLibrary = await CreateLibraryAsync();
        using var admin = AdminClient();
        (await PostRunAsync(admin, RunBody(PhotoResult("c1", "apple_model", [ReadItem("Secret Cart Title")])))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var callers = new List<(string Name, HttpClient Client, HttpStatusCode Expected)>
        {
            ("anonymous", Factory.CreateClient(), HttpStatusCode.Unauthorized),
            ("library owner", As(library, LibraryRole.Owner), HttpStatusCode.Forbidden),
            ("library editor", As(library, LibraryRole.Editor), HttpStatusCode.Forbidden),
            ("library viewer", As(library, LibraryRole.Viewer), HttpStatusCode.Forbidden),
            ("another library's owner", As(otherLibrary, LibraryRole.Owner), HttpStatusCode.Forbidden),
            ("admin", admin, HttpStatusCode.OK),
        };
        var routes = new List<(string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)>
        {
            ("GET golden", c => c.GetAsync($"{Base}/golden")),
            ("GET golden image", c => c.GetAsync($"{Base}/golden/c1/image")),
            ("POST runs", c => PostRunAsync(c, RunBody(PhotoResult("c1", "apple_model", [ReadItem("Secret Cart Title")])))),
            ("GET latest", c => c.GetAsync($"{Base}/runs/latest")),
        };

        foreach (var (callerName, client, expected) in callers)
        {
            foreach (var (routeName, call) in routes)
            {
                using var response = await call(client);
                response.StatusCode.ShouldBe(expected, $"{routeName} as {callerName}");
            }
        }
    }

    [Theory]
    [InlineData("..%5Cx")] // ..\x
    [InlineData("a%2Fb")] // a/b
    [InlineData("%2e%2e")]
    [InlineData("..")]
    [InlineData("%2e%2e%2fsecret")]
    [InlineData("..%2F..%2Fsecret")]
    [InlineData("C%3A%5CWindows%5Cwin.ini")]
    [InlineData("%2Fetc%2Fpasswd")]
    [InlineData("%5C%5Cserver%5Cshare%5Cx")]
    [InlineData("shelf%2Fs1")]
    [InlineData("c1.jpg")]
    [InlineData("c1.truth")]
    [InlineData("not-in-the-folder")]
    [InlineData("%E2%80%A6")]
    public async Task A_path_that_is_not_a_plain_listed_id_is_not_found_and_reads_nothing_outside_the_golden_folder(string id)
    {
        Golden.AddSingle("c1", CartTruth);
        Golden.AddShelf("s1", [new SyntheticGolden.ShelfItem("A Shelf Title", "game")]);
        await File.WriteAllBytesAsync(Path.Combine(Golden.Root, "secret.jpg"), Encoding.ASCII.GetBytes("OUTSIDE-THE-GOLDEN-FOLDER"));
        using var admin = AdminClient();

        using var response = await admin.GetAsync($"{Base}/golden/{id}/image");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("OUTSIDE-THE-GOLDEN-FOLDER");
    }

    [Fact]
    public async Task An_over_long_name_is_not_found_even_when_a_file_by_that_name_exists_in_the_folder()
    {
        var longId = new string('a', 65);
        Golden.AddSingle(longId, CartTruth);
        Golden.AddSingle("c1", CartTruth);
        using var admin = AdminClient();

        using var response = await admin.GetAsync($"{Base}/golden/{longId}/image");
        var list = await admin.GetStringAsync($"{Base}/golden");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        list.ShouldNotContain(longId);
        list.ShouldContain("c1");
    }

    [Fact]
    public async Task The_image_route_serves_only_pictures_in_the_golden_folder_never_a_truth_file_or_anything_else()
    {
        Golden.AddSingle("c1", CartTruth);
        Golden.AddShelf("s1", [new SyntheticGolden.ShelfItem("A Shelf Title", "game")], extension: ".jpeg");
        Golden.AddFile("single", "notes.txt", "PRIVATE-NOTES");
        Golden.AddFile("single", "diagram.png", "PRIVATE-PNG");
        Golden.AddFile("single", "secrets.json", "PRIVATE-JSON");
        Golden.AddFile("single", "c1.truth.draft.json", "PRIVATE-DRAFT");
        using var admin = AdminClient();

        using var picture = await admin.GetAsync($"{Base}/golden/c1/image");
        var bytes = await picture.Content.ReadAsByteArrayAsync();

        picture.StatusCode.ShouldBe(HttpStatusCode.OK);
        picture.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        picture.Headers.CacheControl!.Private.ShouldBeTrue();
        picture.Headers.CacheControl.Public.ShouldBeFalse();
        bytes.ShouldBe(SyntheticGolden.PictureBytes);

        foreach (var id in new[] { "c1.truth", "c1.truth.draft", "notes", "diagram", "secrets", "truth", "c1.truth.json" })
        {
            using var other = await admin.GetAsync($"{Base}/golden/{id}/image");
            other.StatusCode.ShouldBe(HttpStatusCode.NotFound, id);
            (await other.Content.ReadAsStringAsync()).ShouldNotContain("PRIVATE");
        }

        var list = await admin.GetStringAsync($"{Base}/golden");
        list.ShouldNotContain("notes");
        list.ShouldNotContain("diagram");
        list.ShouldNotContain("secrets");
        list.ShouldNotContain("PRIVATE");
    }

    [Fact]
    public async Task The_committed_result_file_and_the_logs_contain_no_item_titles_no_photo_bytes_and_no_file_system_paths()
    {
        const string secretTruth = "Zorblax Prime Secret Title";
        const string secretRead = "Quillfeather Misread Words";
        Golden.AddSingle("c1", CartTruth with { Title = secretTruth });
        Golden.AddShelf("s1", [new SyntheticGolden.ShelfItem(secretTruth, "game", "Nintendo 64"), new SyntheticGolden.ShelfItem("Another Private Name", "game")]);
        await CatalogAsync(secretTruth, ScanData.Upc(7001));
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(
            PhotoResult("c1", "apple_model", [ReadItem(secretRead, platform: "Nintendo 64", completeness: "cart_only")]),
            PhotoResult("s1", "apple_model", [ReadItem(secretTruth), ReadItem("Another Private Name Misspelt")]),
            PhotoResult("s1", "vision_pipeline", [ReadItem(secretTruth)])));

        var files = ResultFiles();
        files.ShouldNotBeEmpty("a run writes evals results as <date>-<sha>.json");
        var pictureStart = Convert.ToBase64String(SyntheticGolden.PictureBytes)[..40];
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file);
            text.ShouldNotContain(secretTruth);
            text.ShouldNotContain(secretRead);
            text.ShouldNotContain("Another Private Name");
            text.ShouldNotContain(pictureStart);
            text.ShouldNotContain(Golden.Root);
            text.ShouldNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            text.ShouldNotContain("\\\\"); // an escaped Windows path separator
        }

        var logs = string.Join('\n', Logs.Lines);
        logs.ShouldNotContain(secretTruth);
        logs.ShouldNotContain(secretRead);
        logs.ShouldNotContain("Another Private Name");
        logs.ShouldNotContain(Golden.Root);
        logs.ShouldNotContain(Golden.Golden);
    }

    public static TheoryData<string> BadBodies => new()
    {
        "{ not json",
        "[]",
        "null",
        "{}",
        """{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":"nope"}""",
        """{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[]}""",
        """{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[{"photoId":"..\\x","engine":"apple_model","outcome":"ok","items":[],"latencyMs":1,"thermalState":"nominal"}]}""",
        """{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[{"photoId":"c1","engine":"quantum","outcome":"ok","items":[],"latencyMs":1,"thermalState":"nominal"}]}""",
        """{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[{"photoId":"c1","engine":"apple_model","outcome":"ok","items":[],"latencyMs":-5,"thermalState":"nominal"}]}""",
        """{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[{"photoId":"c1","engine":"apple_model","outcome":"ok","items":[],"latencyMs":1,"thermalState":"on_fire"}]}""",
        """{"device":{"model":"","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[{"photoId":"c1","engine":"apple_model","outcome":"ok","items":[],"latencyMs":1,"thermalState":"nominal"}]}""",
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_malformed_run_body_is_refused_with_a_problem_and_nothing_is_written(string body)
    {
        Golden.AddSingle("c1", CartTruth);
        Golden.AddShelf("untruthed", null);
        var goldenBefore = SyntheticGolden.Files(Golden.Golden);
        using var admin = AdminClient();

        using var response = await admin.PostAsync($"{Base}/runs", new StringContent(body, Encoding.UTF8, "application/json"));

        ((int)response.StatusCode).ShouldBeInRange(400, 499);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
        SyntheticGolden.Files(Golden.Golden).ShouldBe(goldenBefore);
    }

    [Fact]
    public async Task An_oversize_run_body_is_refused_with_a_problem_and_nothing_is_written()
    {
        Golden.AddSingle("c1", CartTruth);
        Golden.AddShelf("untruthed", null);
        var goldenBefore = SyntheticGolden.Files(Golden.Golden);
        using var admin = AdminClient();
        var padding = new string('x', 2 * 1024 * 1024 + 10);
        var body = $$"""{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"{{padding}}","results":[]}""";

        using var response = await admin.PostAsync($"{Base}/runs", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
        SyntheticGolden.Files(Golden.Golden).ShouldBe(goldenBefore);
    }

    [Fact]
    public async Task An_oversize_run_body_with_no_declared_length_is_refused_too()
    {
        Golden.AddSingle("c1", CartTruth);
        using var admin = AdminClient();
        var padding = new string('x', 2 * 1024 * 1024 + 10);
        var body = $$"""{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"{{padding}}","results":[]}""";
        using var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/runs") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        using var response = await admin.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_body_that_is_not_json_is_refused_with_a_problem()
    {
        Golden.AddSingle("c1", CartTruth);
        using var admin = AdminClient();

        using var response = await admin.PostAsync($"{Base}/runs", new StringContent("<run/>", Encoding.UTF8, "text/xml"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }
}
