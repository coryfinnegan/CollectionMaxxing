using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Infrastructure.Providers.Fetching;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CollectionMaxxing.Api.Tests.Hardening;

/// <summary>
/// API7 (server-side request forgery): the server only fetches covers and artwork from the provider hosts, over https, and never lets a
/// redirect, a literal address or a name that resolves somewhere private take it anywhere else. The tests that need a real server use loopback
/// WireMock, so they open the policy to loopback; the address check is then tested on its own with the real rule.
/// </summary>
public sealed class OutboundFetchTests : IDisposable
{
    private readonly WireMockServer _server = LoopbackWireMock.Start();

    private string Url(string path) => _server.Url + path;

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task An_allowed_host_over_the_test_policy_is_fetched()
    {
        _server.Given(Request.Create().WithPath("/c.jpg")).RespondWith(Response.Create().WithBody([1, 2, 3]).WithHeader("Content-Type", "image/jpeg"));

        var result = await Fetcher(Open()).FetchAsync(new Uri(Url("/c.jpg")), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.Ok);
        result.Bytes.ShouldBe([1, 2, 3]);
        result.ContentType.ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task A_host_that_is_not_on_the_list_is_never_contacted()
    {
        var result = await Fetcher(Open() with { AllowHost = _ => false }).FetchAsync(new Uri(Url("/c.jpg")), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.Refused);
        _server.LogEntries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("http://covers.openlibrary.org/b/id/1-L.jpg")]
    [InlineData("ftp://covers.openlibrary.org/b/id/1-L.jpg")]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("https://user:pass@covers.openlibrary.org/b/id/1-L.jpg")]
    [InlineData("https://covers.openlibrary.org:8443/b/id/1-L.jpg")]
    [InlineData("https://evil.example/b/id/1-L.jpg")]
    [InlineData("https://covers.openlibrary.org.evil.example/b/id/1-L.jpg")]
    public async Task The_real_policy_refuses_other_schemes_ports_users_and_hosts(string url)
    {
        var result = await Fetcher(FetchPolicy.Covers).FetchAsync(new Uri(url), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.Refused);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_refused()
    {
        _server.Given(Request.Create().WithPath("/c.jpg")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", "http://evil.example/steal.jpg"));

        var result = await Fetcher(Open() with { AllowHost = host => host == "127.0.0.1" }).FetchAsync(new Uri(Url("/c.jpg")), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.Refused);
    }

    [Theory]
    [InlineData("http://10.0.0.5/x.jpg")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.168.1.1/x.jpg")]
    [InlineData("http://100.100.100.100/x.jpg")]
    public async Task A_redirect_to_a_private_or_link_local_address_is_refused_even_if_its_host_were_listed(string target)
    {
        _server.Given(Request.Create().WithPath("/c.jpg")).RespondWith(Response.Create().WithStatusCode(301).WithHeader("Location", target));
        var policy = Open(); // the test server (loopback) is the only address let in

        var result = await Fetcher(policy).FetchAsync(new Uri(Url("/c.jpg")), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.Refused);
        _server.LogEntries.Count.ShouldBe(1); // only the first request was made
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_refused()
    {
        var policy = FetchPolicy.Covers with { AllowHost = host => host == "localhost", AllowHttp = true };
        var uri = new UriBuilder(Url("/c.jpg")) { Host = "localhost" }.Uri;

        var result = await Fetcher(policy).FetchAsync(uri, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.Refused);
        _server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Redirects_are_followed_a_few_times_and_then_stop()
    {
        _server.Given(Request.Create().WithPath("/loop")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", Url("/loop")));

        var result = await Fetcher(Open() with { MaxRedirects = 3 }).FetchAsync(new Uri(Url("/loop")), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.TooManyRedirects);
        _server.LogEntries.Count.ShouldBe(4); // the first request and three redirects
    }

    [Fact]
    public async Task An_allowed_redirect_is_followed()
    {
        _server.Given(Request.Create().WithPath("/old")).RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", "/new.jpg"));
        _server.Given(Request.Create().WithPath("/new.jpg")).RespondWith(Response.Create().WithBody([9]).WithHeader("Content-Type", "image/png"));

        var result = await Fetcher(Open()).FetchAsync(new Uri(Url("/old")), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.Ok);
        result.Bytes.ShouldBe([9]);
    }

    [Fact]
    public async Task A_body_over_the_size_cap_is_refused_whether_or_not_it_says_how_long_it_is()
    {
        _server.Given(Request.Create().WithPath("/big.jpg")).RespondWith(Response.Create().WithBody(new byte[2048]));

        var result = await Fetcher(Open() with { MaxBytes = 1024 }).FetchAsync(new Uri(Url("/big.jpg")), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.TooLarge);
        result.Bytes.ShouldBeNull();
    }

    [Fact]
    public async Task An_answer_that_is_an_error_is_a_failure_not_data()
    {
        _server.Given(Request.Create().WithPath("/gone.jpg")).RespondWith(Response.Create().WithStatusCode(404).WithBody("<html>not found</html>"));

        var result = await Fetcher(Open()).FetchAsync(new Uri(Url("/gone.jpg")), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(FetchOutcome.Failed);
        result.Bytes.ShouldBeNull();
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.1.2.3", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.0.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)] // NAT64 wrapping 10.0.0.1
    [InlineData("64:ff9b::7f00:1", false)] // NAT64 wrapping 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe", false)] // NAT64 wrapping 169.254.169.254
    [InlineData("2002:a00:1::", false)] // 6to4 wrapping 10.0.0.1
    [InlineData("2002:c0a8:101::1", false)] // 6to4 wrapping 192.168.1.1
    [InlineData("::a00:1", false)] // IPv4-compatible 10.0.0.1
    [InlineData("::7f00:1", false)] // IPv4-compatible 127.0.0.1
    [InlineData("64:ff9b::808:808", true)] // NAT64 wrapping 8.8.8.8
    [InlineData("2002:808:808::", true)] // 6to4 wrapping 8.8.8.8
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void The_address_rule_accepts_only_public_addresses(string address, bool expected) =>
        FetchPolicy.IsPublic(IPAddress.Parse(address)).ShouldBe(expected);

    [Fact]
    public async Task A_body_that_stalls_after_the_headers_fails_within_the_time_limit()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, stop.Token);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: 100\r\n\r\nabc"u8.ToArray(), stop.Token);
            await Task.Delay(Timeout.Infinite, stop.Token); // and then nothing more, ever
        }, stop.Token);
        using var test = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        test.CancelAfter(TimeSpan.FromSeconds(20));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await Fetcher(Open() with { Timeout = TimeSpan.FromSeconds(1) }).FetchAsync(new Uri($"http://127.0.0.1:{port}/slow.jpg"), test.Token);
        watch.Stop();

        result.Outcome.ShouldBe(FetchOutcome.Failed);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => server);
    }

    [Fact]
    public void The_cover_policy_lists_exactly_the_cover_hosts()
    {
        FetchPolicy.Covers.AllowHost("covers.openlibrary.org").ShouldBeTrue();
        FetchPolicy.Covers.AllowHost("IMAGE.TMDB.ORG").ShouldBeTrue();
        FetchPolicy.Covers.AllowHost("example.com").ShouldBeFalse();
        FetchPolicy.Covers.AllowHost("localhost").ShouldBeFalse();
        FetchPolicy.Covers.AllowHttp.ShouldBeFalse();
    }

    /// <summary>Loopback WireMock is the only place the tests fetch from, so they are let in.</summary>
    private static FetchPolicy Open() => FetchPolicy.Covers with { AllowHost = _ => true, AllowHttp = true, AddressAllowed = IPAddress.IsLoopback };

    private static GuardedFetcher Fetcher(FetchPolicy policy) => new(policy);
}
