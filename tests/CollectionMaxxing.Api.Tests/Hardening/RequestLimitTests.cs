using System.Net;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Scanning.Events;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Tests.Hardening;

/// <summary>API4 (unrestricted resource consumption): batches, page sizes and time are bounded. (Upload sizes and shapes: Scanning/Photo/PhotoUploadTests.)</summary>
public class RequestLimitTests : ScanTest
{
    public RequestLimitTests(ApiFixture api)
        : base(api)
    {
        // A one-second request time limit, so "the stream outlives the limit" can be seen in a couple of seconds.
        Settings["Security:RequestTimeoutSeconds"] = "1";
        ConfigureServices = services => services.AddSingleton(new ScanEventOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            Heartbeat = TimeSpan.FromMilliseconds(200),
        });
    }

    [Fact]
    public async Task A_barcode_batch_of_fifty_one_codes_is_refused_and_fifty_is_accepted()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        object[] Codes(int count) => [.. Enumerable.Range(0, count).Select(i => Code(ScanData.Gtin13(ScanData.Upc(9000 + i))))];

        var tooMany = await SubmitAsync(editor, sessionId, "fifty-one", Codes(51));
        var allowed = await SubmitAsync(editor, sessionId, "fifty", Codes(50));

        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooMany.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await tooMany.Content.ReadAsStringAsync()).ShouldContain("scan.too_many_codes");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("items")]
    [InlineData("items:deleted")]
    [InlineData("feature-prints")]
    [InlineData("ownership/snapshot")]
    public async Task A_list_asked_for_an_absurd_page_size_is_clamped_or_refused_never_a_crash(string list)
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);

        foreach (var limit in new[] { int.MaxValue.ToString(), "-5", "0", "99999999999999999999", "abc" })
        {
            var response = await editor.GetAsync($"/api/v1/libraries/{library.Id}/{list}?limit={limit}");

            response.StatusCode.ShouldBeOneOf([HttpStatusCode.OK, HttpStatusCode.BadRequest], $"{list} limit={limit}");
        }
    }

    [Fact]
    public async Task Catalogue_search_clamps_the_page_size()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);

        var response = await viewer.GetAsync("/api/v1/catalog/search?q=zelda&limit=100000");

        response.StatusCode.ShouldBeOneOf([HttpStatusCode.OK, HttpStatusCode.BadRequest]);
    }

    [Fact]
    public void Every_request_has_a_time_limit_set_in_configuration()
    {
        var timeouts = Api.Factory.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;

        timeouts.DefaultPolicy.ShouldNotBeNull();
        timeouts.DefaultPolicy!.Timeout.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task The_event_stream_stays_open_past_the_request_time_limit()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await using var events = await SseClient.ConnectAsync(editor, SessionUrl(sessionId) + "/events");

        var beats = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2.5))
        {
            var next = await events.NextAsync(TimeSpan.FromSeconds(5));
            next.ShouldNotBeNull("the server ended the stream when the time limit passed");
            beats += next!.IsComment ? 1 : 0;
        }

        beats.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_event_stream_route_is_exempt_from_the_request_time_limit()
    {
        EndpointOf("GET", "/events").Metadata.GetMetadata<DisableRequestTimeoutAttribute>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData("POST", "/photo")]
    [InlineData("POST", "/evals/runs")]
    public void Routes_that_take_a_big_upload_have_a_longer_time_limit_than_the_default(string method, string route)
    {
        var attribute = EndpointOf(method, route).Metadata.GetMetadata<RequestTimeoutAttribute>().ShouldNotBeNull();
        var timeouts = Factory.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;

        timeouts.Policies.ShouldContainKey(attribute.PolicyName!);
        // 4 MB over a poor mobile link (0.5 Mbit/s) takes about a minute; allow at least five.
        timeouts.Policies[attribute.PolicyName!].Timeout!.Value.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    private Microsoft.AspNetCore.Routing.RouteEndpoint EndpointOf(string method, string routeEnd) =>
        Factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText!.EndsWith(routeEnd, StringComparison.Ordinal)
                && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains(method));
}
