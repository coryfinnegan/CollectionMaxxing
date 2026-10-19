using System.Net;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CollectionMaxxing.Api.Tests.AppDownload;

/// <summary>
/// Settings > Get the iPhone app (OPS-4): the deployer copies the checked .ipa to <c>ipa/CollectionMaxxing.ipa</c> in the files volume and
/// these two routes hand it to signed-in people. The path is fixed, so there is nothing in a request that can name another file.
/// </summary>
public class IpaDownloadTests(ApiFixture api) : ApiTest(api)
{
    private const string Download = "/api/v1/app/ipa";
    private const string Info = "/api/v1/app/ipa/info";
    private const string Sum = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-ipa-tests", Guid.NewGuid().ToString("N"));

    public override ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
        return base.DisposeAsync();
    }

    private string IpaFolder => Path.Combine(_root, "ipa");

    private string IpaPath => Path.Combine(IpaFolder, "CollectionMaxxing.ipa");

    private void PutIpa(byte[] bytes, string? checksum = null)
    {
        Directory.CreateDirectory(IpaFolder);
        File.WriteAllBytes(IpaPath, bytes);
        if (checksum is not null)
        {
            File.WriteAllText(IpaPath + ".sha256", checksum);
        }
    }

    private HttpClient Client(Guid? userId)
    {
        var factory = Api.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Files:Root"] = _root })));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, id.ToString());
        }

        return client;
    }

    [Theory]
    [InlineData(LibraryRole.Viewer)]
    [InlineData(LibraryRole.Editor)]
    [InlineData(LibraryRole.Owner)]
    public async Task Every_role_downloads_the_file_byte_for_byte_as_an_attachment(LibraryRole role)
    {
        var library = await CreateLibraryAsync();
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        PutIpa(bytes);

        var response = await Client(library.UserWithRole(role)).GetAsync(Download);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition.FileName.ShouldBe("CollectionMaxxing.ipa");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task Head_reports_the_size_and_date_without_a_body()
    {
        var library = await CreateLibraryAsync();
        PutIpa(new byte[1234]);
        var modified = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(IpaPath, modified);

        var response = await Client(library.OwnerId).SendAsync(new HttpRequestMessage(HttpMethod.Head, Download));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.ShouldBe(1234);
        response.Content.Headers.LastModified.ShouldBe(new DateTimeOffset(modified));
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Download)]
    [InlineData(Info)]
    public async Task Anonymous_gets_401(string route)
    {
        PutIpa([1, 2, 3]);

        var response = await Client(null).GetAsync(route);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task With_no_file_synced_the_download_is_a_friendly_404_and_the_info_says_not_available()
    {
        var library = await CreateLibraryAsync();
        var client = Client(library.OwnerId);

        var download = await client.GetAsync(Download);
        download.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(download)).GetProperty("detail").GetString()!.ShouldContain("after the next release");

        var info = await client.GetAsync(Info);
        info.StatusCode.ShouldBe(HttpStatusCode.OK); // a normal answer, so the browser console stays quiet
        (await ReadJsonAsync(info)).GetProperty("available").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Info_gives_the_size_the_date_and_the_checksum()
    {
        var library = await CreateLibraryAsync();
        PutIpa(new byte[2048], $"{Sum.ToUpperInvariant()}  CollectionMaxxing.ipa\n");
        var modified = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(IpaPath, modified);

        var response = await Client(library.UserWithRole(LibraryRole.Viewer)).GetAsync(Info);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("available").GetBoolean().ShouldBeTrue();
        body.GetProperty("sizeBytes").GetInt64().ShouldBe(2048);
        body.GetProperty("modifiedAt").GetDateTimeOffset().ShouldBe(new DateTimeOffset(modified));
        body.GetProperty("sha256").GetString().ShouldBe(Sum);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a checksum\n")]
    public async Task Info_has_no_checksum_when_the_file_is_missing_or_unreadable(string? text)
    {
        var library = await CreateLibraryAsync();
        PutIpa([1], text);

        var body = await ReadJsonAsync(await Client(library.OwnerId).GetAsync(Info));

        body.GetProperty("sha256").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("/api/v1/app/ipa/..%2f..%2fsecret.txt")]
    [InlineData("/api/v1/app/ipa/other.ipa")]
    [InlineData("/api/v1/app/ipa/CollectionMaxxing.ipa.sha256")]
    [InlineData("/api/v1/app/ipa?path=../secret.txt")]
    [InlineData("/api/v1/app/ipa?file=other.ipa")]
    public async Task No_request_can_make_the_route_read_any_other_file(string route)
    {
        var library = await CreateLibraryAsync();
        PutIpa([1, 2, 3], $"{Sum}  CollectionMaxxing.ipa");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "secret");
        File.WriteAllBytes(Path.Combine(IpaFolder, "other.ipa"), [9, 9, 9]);

        var response = await Client(library.OwnerId).GetAsync(route);
        var body = await response.Content.ReadAsByteArrayAsync();

        Encoding.UTF8.GetString(body).ShouldNotContain("secret");
        body.ShouldNotContain((byte)9);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            body.ShouldBe([1, 2, 3]); // the query is ignored: it is always the one fixed file
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }
}
