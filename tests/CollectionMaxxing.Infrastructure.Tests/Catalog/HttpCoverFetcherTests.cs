using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Infrastructure.Catalog;
using CollectionMaxxing.Infrastructure.Providers.Fetching;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CollectionMaxxing.Infrastructure.Tests.Catalog;

/// <summary>What the cover fetcher reports for each way a download can end, against loopback fakes.</summary>
public class HttpCoverFetcherTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private readonly WireMockServer _covers = Providers.LoopbackWireMock.Start();
    private readonly WireMockServer _archive = Providers.LoopbackWireMock.Start();

    private static FetchPolicy Policy(Func<string, bool>? allowHost = null) => new()
    {
        AllowHost = allowHost ?? (_ => true),
        AllowHttp = true, // loopback fakes only
        AddressAllowed = _ => true,
    };

    public void Dispose()
    {
        _covers.Dispose();
        _archive.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Follows_a_cover_redirect_chain_to_the_image()
    {
        _covers.Given(Request.Create().WithPath("/b/id/42-L.jpg").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{_archive.Url}/file/42-L.jpg"));
        _archive.Given(Request.Create().WithPath("/file/42-L.jpg").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "image/jpeg").WithBody(Jpeg));
        using var fetcher = new HttpCoverFetcher(Policy());

        var result = await fetcher.FetchAsync($"{_covers.Url}/b/id/42-L.jpg", TestContext.Current.CancellationToken);

        result.Cover.ShouldNotBeNull();
        result.Cover.Bytes.ToArray().ShouldBe(Jpeg);
        result.Cover.ContentType.ShouldBe("image/jpeg");
        result.FailureReason.ShouldBeNull();
    }

    [Fact]
    public async Task A_redirect_to_a_host_outside_the_policy_comes_back_refused()
    {
        // Both fakes listen on 127.0.0.1; localhost names the same socket under a host the policy refuses.
        var elsewhere = _archive.Url!.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        _covers.Given(Request.Create().WithPath("/b/id/42-L.jpg").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{elsewhere}/file/42-L.jpg"));
        using var fetcher = new HttpCoverFetcher(Policy(host => host == "127.0.0.1"));

        var result = await fetcher.FetchAsync($"{_covers.Url}/b/id/42-L.jpg", TestContext.Current.CancellationToken);

        result.Cover.ShouldBeNull();
        result.FailureReason.ShouldBe("refused");
    }

    [Fact]
    public async Task A_cover_over_the_size_cap_comes_back_too_large()
    {
        _covers.Given(Request.Create().WithPath("/big.jpg").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "image/jpeg").WithBody(Jpeg));
        using var fetcher = new HttpCoverFetcher(Policy() with { MaxBytes = 4 });

        var result = await fetcher.FetchAsync($"{_covers.Url}/big.jpg", TestContext.Current.CancellationToken);

        result.Cover.ShouldBeNull();
        result.FailureReason.ShouldBe("too-large");
    }

    [Fact]
    public async Task Bytes_that_are_no_image_come_back_not_an_image()
    {
        _covers.Given(Request.Create().WithPath("/cover.jpg").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "image/jpeg").WithBody("<html>nope</html>"));
        using var fetcher = new HttpCoverFetcher(Policy());

        var result = await fetcher.FetchAsync($"{_covers.Url}/cover.jpg", TestContext.Current.CancellationToken);

        result.Cover.ShouldBeNull();
        result.FailureReason.ShouldBe("not-an-image");
    }

    [Fact]
    public async Task A_missing_cover_comes_back_fetch_failed()
    {
        _covers.Given(Request.Create().WithPath("/gone.jpg").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        using var fetcher = new HttpCoverFetcher(Policy());

        var result = await fetcher.FetchAsync($"{_covers.Url}/gone.jpg", TestContext.Current.CancellationToken);

        result.Cover.ShouldBeNull();
        result.FailureReason.ShouldBe("fetch-failed");
    }

    [Fact]
    public async Task A_redirect_loop_comes_back_too_many_redirects()
    {
        _covers.Given(Request.Create().WithPath("/loop.jpg").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{_covers.Url}/loop.jpg"));
        using var fetcher = new HttpCoverFetcher(Policy());

        var result = await fetcher.FetchAsync($"{_covers.Url}/loop.jpg", TestContext.Current.CancellationToken);

        result.Cover.ShouldBeNull();
        result.FailureReason.ShouldBe("too-many-redirects");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/cover.jpg")]
    public async Task Garbage_comes_back_invalid_url(string url)
    {
        using var fetcher = new HttpCoverFetcher(Policy());

        var result = await fetcher.FetchAsync(url, TestContext.Current.CancellationToken);

        result.Cover.ShouldBeNull();
        result.FailureReason.ShouldBe("invalid-url");
    }
}
