using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// <c>POST /api/admin/evals/golden/{set}/{id}</c>: the owner adds a golden photo from the phone, at full size, straight into the PC's golden
/// folder. The bytes decide what the picture is (never the header the phone sent), the file is created and never replaced, an id is unique across
/// both sets, and nothing is ever written outside the golden folder. The id check answers 400 (not 404): the caller is the signed-in owner, and a
/// name the folder cannot hold is a mistake worth explaining.
/// </summary>
public class AddGoldenPhotoTests(ApiFixture api) : EvalTest(api)
{
    private const int Cap = 40 * 1024 * 1024;

    private static readonly SyntheticGolden.SingleItem CartTruth =
        new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    private static string Url(string set, string id) => $"{Base}/golden/{set}/{id}";

    private static byte[] Jpeg(int length = 5000, byte fill = 0x11)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        return bytes;
    }

    /// <summary>The start of an ISO base media file: a <c>ftyp</c> box with the given major brand, then filler.</summary>
    private static byte[] Isobmff(string brand, int length = 5000)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 24);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes(brand).CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("mif1").CopyTo(bytes, 16);
        Encoding.ASCII.GetBytes("heic").CopyTo(bytes, 20);
        Array.Fill(bytes, (byte)0x22, 24, length - 24);
        return bytes;
    }

    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    /// <summary>The bytes as a body, with a header the test chooses (the endpoint must not believe it).</summary>
    private static ByteArrayContent Body(byte[] bytes, string contentType = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    private static async Task<JsonElement> ListAsync(HttpClient admin) => JsonSerializer.Deserialize<JsonElement>(await admin.GetStringAsync($"{Base}/golden"));

    // ---- what is accepted --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("single")]
    [InlineData("shelf")]
    public async Task A_jpeg_is_saved_whole_as_a_jpg_in_its_set_folder_and_listed_with_its_size(string set)
    {
        var bytes = Jpeg(123_457);
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url(set, "cover-01"), Body(bytes));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var saved = Path.Combine(Golden.Golden, set, "cover-01.jpg");
        (await File.ReadAllBytesAsync(saved)).ShouldBe(bytes);
        var created = await ReadJsonAsync(response);
        created.GetProperty("id").GetString().ShouldBe("cover-01");
        created.GetProperty("set").GetString().ShouldBe(set);
        created.GetProperty("hasTruth").GetBoolean().ShouldBeFalse();
        created.GetProperty("bytes").GetInt64().ShouldBe(bytes.Length);
        response.Headers.Location!.ToString().ShouldEndWith("/api/admin/evals/golden/cover-01/image");
    }

    [Fact]
    public async Task The_saved_photo_appears_in_the_golden_list_and_is_served_back_unchanged()
    {
        Golden.AddSingle("c1", CartTruth);
        var bytes = Jpeg();
        using var admin = AdminClient();
        (await admin.PostAsync(Url("shelf", "shelf-01"), Body(bytes))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var list = await ListAsync(admin);
        using var image = await admin.GetAsync($"{Base}/golden/shelf-01/image");

        list.EnumerateArray().Select(p => (p.GetProperty("id").GetString(), p.GetProperty("set").GetString(), p.GetProperty("hasTruth").GetBoolean()))
            .ShouldBe([("c1", "single", true), ("shelf-01", "shelf", false)], ignoreOrder: true);
        image.StatusCode.ShouldBe(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        (await image.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
    }

    [Theory]
    [InlineData("heic")]
    [InlineData("heix")]
    [InlineData("mif1")]
    [InlineData("msf1")]
    public async Task A_heic_is_saved_whole_as_a_heic_and_served_as_one(string brand)
    {
        var bytes = Isobmff(brand, 70_001);
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("single", "cover-02"), Body(bytes));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await File.ReadAllBytesAsync(Path.Combine(Golden.Golden, "single", "cover-02.heic"))).ShouldBe(bytes);
        File.Exists(Path.Combine(Golden.Golden, "single", "cover-02.jpg")).ShouldBeFalse();
        using var image = await admin.GetAsync($"{Base}/golden/cover-02/image");
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/heic");
        (await image.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("image/png")]
    [InlineData("application/json")]
    public async Task The_bytes_decide_the_kind_never_the_header_a_jpeg_sent_under_any_header_is_a_jpeg(string header)
    {
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("single", "c2"), Body(Jpeg(), header));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        File.Exists(Path.Combine(Golden.Golden, "single", "c2.jpg")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_photo_sent_with_no_content_type_at_all_is_still_saved()
    {
        using var admin = AdminClient();
        using var content = new ByteArrayContent(Jpeg());
        content.Headers.ContentType = null;

        using var response = await admin.PostAsync(Url("single", "c3"), content);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        File.Exists(Path.Combine(Golden.Golden, "single", "c3.jpg")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_valid_id_may_have_capitals_underscores_dashes_and_digits_up_to_64_characters()
    {
        using var admin = AdminClient();
        var longest = "A_" + new string('b', 62);

        (await admin.PostAsync(Url("shelf", "Shelf_01-B"), Body(Jpeg()))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PostAsync(Url("shelf", longest), Body(Jpeg()))).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await ListAsync(admin)).EnumerateArray().Select(p => p.GetProperty("id").GetString()).ShouldBe(["Shelf_01-B", longest], ignoreOrder: true);
    }

    [Fact]
    public async Task A_set_folder_that_does_not_exist_yet_is_made_inside_the_golden_folder()
    {
        Directory.Delete(Path.Combine(Golden.Golden, "shelf"));
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("shelf", "shelf-01"), Body(Jpeg()));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        File.Exists(Path.Combine(Golden.Golden, "shelf", "shelf-01.jpg")).ShouldBeTrue();
    }

    // ---- what is refused ----------------------------------------------------------------------------------------------------

    public static TheoryData<string, byte[]> NotPictures => new()
    {
        { "text", Encoding.UTF8.GetBytes("This is not a picture, whatever the header says.") },
        { "png", Png() },
        { "gif", Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00") },
        { "empty", [] },
        { "a jpeg that stops after two bytes", [0xFF, 0xD8] },
        { "html", Encoding.UTF8.GetBytes("<!doctype html><script>alert(1)</script>") },
        { "a movie (ftyp mp42)", Isobmff("mp42") },
        { "an avif (ftyp avif)", Isobmff("avif") },
        { "a box that is not ftyp", [0, 0, 0, 24, (byte)'m', (byte)'o', (byte)'o', (byte)'v', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0, 0, 0] },
        { "a ftyp cut short", [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i'] },
    };

    [Theory]
    [MemberData(nameof(NotPictures))]
    public async Task Anything_that_is_not_a_jpeg_or_a_heic_is_refused_415_even_under_an_image_header_and_nothing_is_written(string what, byte[] bytes)
    {
        _ = what;
        Golden.AddSingle("c1", CartTruth);
        var before = SyntheticGolden.Files(Golden.Golden);
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("single", "cover-01"), Body(bytes, "image/jpeg"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        SyntheticGolden.Files(Golden.Golden).ShouldBe(before);
    }

    [Theory]
    [InlineData("single", "shelf")]
    [InlineData("shelf", "single")]
    [InlineData("single", "single")]
    [InlineData("shelf", "shelf")]
    public async Task A_taken_id_is_a_409_whichever_set_holds_it_and_the_first_photo_is_left_alone(string existingSet, string newSet)
    {
        var first = existingSet == "single" ? Golden.AddSingle("cover-01", null) : Golden.AddShelf("cover-01", null);
        var firstBytes = await File.ReadAllBytesAsync(first);
        var before = SyntheticGolden.Files(Golden.Golden);
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url(newSet, "cover-01"), Body(Jpeg(777, 0x33)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.id_taken");
        SyntheticGolden.Files(Golden.Golden).ShouldBe(before);
        (await File.ReadAllBytesAsync(first)).ShouldBe(firstBytes);
    }

    [Theory]
    [InlineData("cover-01.jpeg")]
    [InlineData("cover-01.heic")]
    [InlineData("cover-01.png")] // not a golden picture, but the name is used
    [InlineData("cover-01.truth.json")] // a truth file left behind belongs to that name
    [InlineData("cover-01.truth.draft.json")]
    [InlineData("COVER-01.jpg")] // the PC's file system may not tell the two names apart
    public async Task A_taken_id_counts_with_any_extension_and_in_any_letter_case(string existingFile)
    {
        Golden.AddFile("shelf", existingFile, "existing");
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("single", "cover-01"), Body(Jpeg()));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        SyntheticGolden.Files(Golden.Golden).ShouldNotContain("single/cover-01.jpg".Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task A_name_that_only_starts_like_another_is_not_taken()
    {
        Golden.AddSingle("cover-011", null);
        Golden.AddSingle("cover", null);
        using var admin = AdminClient();

        (await admin.PostAsync(Url("single", "cover-01"), Body(Jpeg()))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Two_uploads_of_the_same_id_at_the_same_time_save_one_and_refuse_the_other_even_across_sets()
    {
        using var admin = AdminClient();

        var responses = await Task.WhenAll(
            admin.PostAsync(Url("single", "same-01"), Body(Jpeg(2000, 0x41))),
            admin.PostAsync(Url("shelf", "same-01"), Body(Jpeg(2000, 0x42))),
            admin.PostAsync(Url("single", "same-01"), Body(Jpeg(2000, 0x43))));

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.Conflict]);
        (await ListAsync(admin)).GetArrayLength().ShouldBe(1, "an id that is in both sets would vanish from the listing");
    }

    public static TheoryData<string> BadIds => new()
    {
        "has%20space",
        "c1.jpg",
        "c1.truth",
        "caf%C3%A9",
        "semi;colon",
        "tilde~",
        "con",
        "CON",
        "nul",
        "Aux",
        "prn",
        "com1",
        "LPT9",
        "%E2%80%A6",
    };

    [Theory]
    [MemberData(nameof(BadIds))]
    public async Task An_id_the_folder_cannot_hold_is_a_400_and_nothing_is_written(string id)
    {
        var before = SyntheticGolden.Files(Golden.Root);
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("single", id), Body(Jpeg()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        SyntheticGolden.Files(Golden.Root).ShouldBe(before);
    }

    [Fact]
    public async Task An_id_of_65_characters_is_a_400()
    {
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("single", new string('a', 65)), Body(Jpeg()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("..%5Cx")]
    [InlineData("a%2Fb")]
    [InlineData("%2e%2e%2fsecret")]
    [InlineData("..%2F..%2Fsecret")]
    [InlineData("C%3A%5CWindows%5Cwin.ini")]
    [InlineData("%5C%5Cserver%5Cshare%5Cx")]
    [InlineData("shelf%2Fs1")]
    public async Task A_path_in_place_of_an_id_writes_nothing_anywhere(string id)
    {
        var before = SyntheticGolden.Files(Golden.Root);
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("single", id), Body(Jpeg()));

        ((int)response.StatusCode).ShouldBeInRange(400, 404);
        SyntheticGolden.Files(Golden.Root).ShouldBe(before);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("SINGLE")]
    [InlineData("results")]
    [InlineData("..")]
    public async Task A_set_that_is_neither_single_nor_shelf_is_refused_and_nothing_is_written(string set)
    {
        var before = SyntheticGolden.Files(Golden.Root);
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url(set, "cover-01"), Body(Jpeg()));

        ((int)response.StatusCode).ShouldBeInRange(400, 404);
        SyntheticGolden.Files(Golden.Root).ShouldBe(before);
    }

    [Fact]
    public async Task A_set_name_that_is_not_one_of_the_two_is_a_400_with_a_problem()
    {
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("other", "cover-01"), Body(Jpeg()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.set_invalid");
    }

    // ---- size ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_picture_of_exactly_the_cap_is_accepted()
    {
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("single", "big-01"), Body(Jpeg(Cap)));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        new FileInfo(Path.Combine(Golden.Golden, "single", "big-01.jpg")).Length.ShouldBe(Cap);
    }

    [Fact]
    public async Task A_picture_over_the_cap_is_a_413_and_nothing_is_written()
    {
        var before = SyntheticGolden.Files(Golden.Golden);
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url("single", "big-01"), Body(Jpeg(Cap + 1)));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        SyntheticGolden.Files(Golden.Golden).ShouldBe(before);
    }

    [Fact]
    public async Task A_picture_over_the_cap_with_no_declared_length_is_a_413_too()
    {
        var before = SyntheticGolden.Files(Golden.Golden);
        using var admin = AdminClient();
        using var content = new StreamContent(new MemoryStream(Jpeg(Cap + 1)));
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("single", "big-01")) { Content = content };
        request.Headers.TransferEncodingChunked = true;

        using var response = await admin.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        SyntheticGolden.Files(Golden.Golden).ShouldBe(before);
    }

    [Fact]
    public async Task The_cap_belongs_to_this_route_alone_the_run_upload_keeps_its_2_mb_limit()
    {
        using var admin = AdminClient();
        var padding = new string('x', 2 * 1024 * 1024 + 10);
        var body = $$"""{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"{{padding}}","results":[]}""";

        using var response = await admin.PostAsync($"{Base}/runs", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    // ---- when it is off -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("false", "golden")]
    [InlineData("true", "missing-folder")]
    [InlineData("true", "")]
    public async Task When_the_evals_are_off_or_there_is_no_golden_folder_the_route_is_a_404_before_the_body_is_looked_at(string enabled, string golden)
    {
        Settings["Evals:Enabled"] = enabled;
        Settings["Evals:GoldenPath"] = golden switch
        {
            "golden" => Golden.Golden,
            "missing-folder" => Path.Combine(Golden.Root, "no-such-folder"),
            _ => string.Empty,
        };
        using var admin = AdminClient();

        (await admin.PostAsync(Url("single", "cover-01"), Body(Jpeg()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsync(Url("single", "cover-01"), Body(Encoding.UTF8.GetBytes("not a picture")))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsync(Url("single", "bad%20id"), Body(Jpeg()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsync(Url("single", "cover-01"), Body(Jpeg(Cap + 1)))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        SyntheticGolden.Files(Golden.Golden).ShouldBeEmpty();
        Directory.Exists(Path.Combine(Golden.Root, "no-such-folder")).ShouldBeFalse();
    }

    // ---- who may ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_admin_may_add_a_photo_everyone_else_is_refused_and_nothing_is_written()
    {
        var library = await CreateLibraryAsync();
        var otherLibrary = await CreateLibraryAsync();
        var callers = new List<(string Name, HttpClient Client, HttpStatusCode Expected)>
        {
            ("anonymous", Factory.CreateClient(), HttpStatusCode.Unauthorized),
            ("library owner", As(library, LibraryRole.Owner), HttpStatusCode.Forbidden),
            ("library editor", As(library, LibraryRole.Editor), HttpStatusCode.Forbidden),
            ("library viewer", As(library, LibraryRole.Viewer), HttpStatusCode.Forbidden),
            ("another library's owner", As(otherLibrary, LibraryRole.Owner), HttpStatusCode.Forbidden),
        };

        foreach (var (name, client, expected) in callers)
        {
            using var response = await client.PostAsync(Url("single", "cover-01"), Body(Jpeg()));
            response.StatusCode.ShouldBe(expected, name);
        }

        SyntheticGolden.Files(Golden.Golden).ShouldBeEmpty();
        using var admin = AdminClient();
        (await admin.PostAsync(Url("single", "cover-01"), Body(Jpeg()))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // ---- links and logs -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("single")]
    [InlineData("shelf")]
    public async Task A_set_folder_that_is_a_link_to_outside_is_not_written_through(string set)
    {
        var outside = Path.Combine(Golden.Root, "outside-" + set);
        Directory.CreateDirectory(outside);
        var folder = Path.Combine(Golden.Golden, set);
        Directory.Delete(folder, true);
        FolderLink.Create(folder, outside);
        using var admin = AdminClient();

        using var response = await admin.PostAsync(Url(set, "cover-01"), Body(Jpeg()));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.golden_not_writable");
        Directory.GetFileSystemEntries(outside).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_log_has_the_id_the_set_and_the_size_and_never_the_bytes_or_a_path()
    {
        var bytes = Jpeg(4321, 0x5A);
        using var admin = AdminClient();

        (await admin.PostAsync(Url("shelf", "shelf-07"), Body(bytes))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var logs = string.Join('\n', Logs.Lines);
        var added = logs.Split('\n').Where(l => l.Contains("shelf-07", StringComparison.Ordinal)).ToList();
        added.ShouldContain(l => l.Contains("shelf") && l.Contains("4321"));
        logs.ShouldNotContain(Convert.ToBase64String(bytes)[..40]);
        logs.ShouldNotContain(Golden.Root);
        logs.ShouldNotContain(Golden.Golden);
    }

    [Fact]
    public async Task A_refusal_is_logged_with_its_code_and_not_with_the_name_the_caller_sent()
    {
        using var admin = AdminClient();

        (await admin.PostAsync(Url("single", "bad%0AFORGED%20line"), Body(Jpeg()))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsync(Url("single", "c1"), Body(Encoding.UTF8.GetBytes("text")))).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);

        var logs = Logs.Lines.Where(l => l.Contains("CollectionMaxxing.Api.Evals", StringComparison.Ordinal)).ToList();
        logs.ShouldContain(l => l.Contains("evals.id_invalid"));
        logs.ShouldContain(l => l.Contains("evals.unsupported_picture"));
        logs.ShouldNotContain(l => l.Contains("FORGED"));
    }
}
