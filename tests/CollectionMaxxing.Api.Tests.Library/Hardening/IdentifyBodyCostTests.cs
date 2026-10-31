using System.Net;
using System.Net.Http.Json;
using System.Text;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.RateLimiting;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Api.Tests.Hardening;

/// <summary>
/// An identify call costs one permit per item the endpoint will read from its body. The body is read once, bounded, and what the
/// limiter counted is what the endpoint processes: nothing the limiter did not see can be read, and a body the endpoint refuses
/// outright does no identify work. The limits are lowered so a test needs a handful of items, not hundreds.
/// </summary>
public sealed class IdentifyBodyCostTests(ApiFixture api) : ApiTest(api), IDisposable
{
    private const int MaxBodyBytes = 256 * 1024;

    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private readonly LogCapture _logs = new();

    void IDisposable.Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task A_body_that_names_its_items_twice_costs_the_items_the_endpoint_reads()
    {
        var (client, sessionId) = await StartAsync(permits: 5);
        const string body = """{"items":[],"items":[{},{},{},{},{},{}]}""";

        var response = await PostAsync(client, sessionId, body);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests); // the last items array is the one read: six items, five permits
    }

    [Fact]
    public async Task A_body_over_the_limit_is_413_and_takes_no_identify_permits()
    {
        var (client, sessionId) = await StartAsync(permits: 5);
        var oversize = Padded(MaxBodyBytes + 1, itemCount: 1);

        var refused = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            using var response = await PostAsync(client, sessionId, oversize);
            refused.Add(response.StatusCode);
        }

        refused.ShouldAllBe(s => s == HttpStatusCode.RequestEntityTooLarge);
        (await PostAsync(client, sessionId, Items(5))).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        (await PostAsync(client, sessionId, Items(1))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_body_between_64_kilobytes_and_the_limit_is_counted_like_any_other()
    {
        var (client, sessionId) = await StartAsync(permits: 5);
        var big = Padded(200 * 1024, itemCount: 3);

        var first = await PostAsync(client, sessionId, big); // 3 of 5
        var second = await PostAsync(client, sessionId, big); // 6 of 5

        first.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_body_of_exactly_the_limit_is_counted()
    {
        var (client, sessionId) = await StartAsync(permits: 5);
        var atLimit = Padded(MaxBodyBytes, itemCount: 5);

        (await PostAsync(client, sessionId, atLimit)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        (await PostAsync(client, sessionId, Items(1))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_body_that_is_not_valid_json_is_400_and_costs_one_permit()
    {
        var (client, sessionId) = await StartAsync(permits: 5);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            using var response = await PostAsync(client, sessionId, """{"items":[{},{},{}""");
            statuses.Add(response.StatusCode);
        }

        statuses.Take(5).ShouldAllBe(s => s == HttpStatusCode.BadRequest);
        statuses[5].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_call_the_limit_allows_reaches_the_endpoint_with_the_body_the_limiter_counted()
    {
        var (client, sessionId) = await StartAsync(permits: 50);

        // No snapId: the handler refuses it, which it can only do if it was given the request the limiter had already read.
        using var response = await PostAsync(client, sessionId, Items(3));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.snap_id_required");
    }

    [Fact]
    public async Task A_batch_that_fails_to_bind_is_400_and_costs_the_items_it_claims()
    {
        var (client, sessionId) = await StartAsync(permits: 5);
        const string bad = """{"items":[{"kind":"zzz"},{},{}]}""";

        var first = await PostAsync(client, sessionId, bad); // 3 of 5
        var second = await PostAsync(client, sessionId, bad); // 6 of 5

        first.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_large_batch_that_fails_to_bind_is_refused_at_once_when_it_claims_more_items_than_the_allowance()
    {
        var (client, sessionId) = await StartAsync(permits: 50);
        var claimed = "{\"items\":[{\"kind\":\"zzz\"}" + string.Concat(Enumerable.Repeat(",{}", 4999)) + "]}";

        var response = await PostAsync(client, sessionId, claimed);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_batch_that_fails_to_bind_and_names_its_items_twice_costs_the_last_array()
    {
        var (client, sessionId) = await StartAsync(permits: 5);
        const string body = """{"items":[{}],"Items":[{"kind":"zzz"},{},{},{},{},{}]}""";

        var response = await PostAsync(client, sessionId, body);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests); // six items in the array that wins, five permits
    }

    [Theory]
    [InlineData("""{"items":5}""")]
    [InlineData("""{"items":"zzz"}""")]
    [InlineData("""{"items":null,"kind":"zzz"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"items":[{},{}]} trailing""")]
    [InlineData("""{"items":[{},{},{},{},{},{},{}]""")]
    public async Task A_body_with_no_countable_items_array_costs_one_permit_and_is_400(string body)
    {
        var (client, sessionId) = await StartAsync(permits: 5);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            using var response = await PostAsync(client, sessionId, body);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(5).ShouldAllBe(s => s == HttpStatusCode.BadRequest);
        statuses[5].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_body_that_breaks_off_mid_read_costs_one_identify_permit_and_fails_as_it_always_did()
    {
        var (client, sessionId) = await StartAsync(permits: 3);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 3; i++)
        {
            using var response = await PostBrokenAsync(client, sessionId);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldAllBe(s => s == HttpStatusCode.InternalServerError, string.Join(", ", statuses)); // the exception handler answers the read's own exception
        // Each broken call took a permit before it failed, so the allowance of three is gone.
        (await PostAsync(client, sessionId, Items(1))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_body_that_breaks_off_mid_read_also_costs_the_general_allowance()
    {
        var (client, sessionId) = await StartAsync(permits: 50, generalPermits: 5);

        for (var i = 0; i < 4; i++)
        {
            using var response = await PostBrokenAsync(client, sessionId);
        }

        // One permit went on starting the session, four on the broken calls.
        (await client.GetAsync("/api/v1/libraries", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private static async Task<HttpResponseMessage> PostBrokenAsync(HttpClient client, Guid sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scan-sessions/{sessionId}/identify")
        {
            Content = new StringContent(Items(2), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(BreakBodyFilter.Header, "1");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Makes the body of a call that asks for it fail part-way, the way Kestrel does when fewer bytes arrive than the call declared.</summary>
    private sealed class BreakBodyFilter : IStartupFilter
    {
        public const string Header = "X-Break-Body";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                if (context.Request.Headers.ContainsKey(Header))
                {
                    context.Request.Body = new BreakingStream(context.Request.Body);
                }

                return following(context);
            });
            next(app);
        };
    }

    private sealed class BreakingStream(Stream inner) : Stream
    {
        private bool _gaveSome;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_gaveSome)
            {
                throw new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest);
            }

            _gaveSome = true;
            return await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 10)], cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string Items(int count) =>
        "{\"items\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => $"{{\"localId\":\"r{i}\",\"title\":\"Read {i}\"}}")) + "]}";

    /// <summary>Valid JSON of exactly <paramref name="totalBytes"/> bytes: trailing spaces are valid JSON.</summary>
    private static string Padded(int totalBytes, int itemCount) => Items(itemCount).PadRight(totalBytes);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid sessionId, string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return client.PostAsync($"/api/v1/scan-sessions/{sessionId}/identify", content, TestContext.Current.CancellationToken);
    }

    /// <summary>A signed-in owner with a scan session of their own, on an app whose identify allowance is <paramref name="permits"/> items.</summary>
    private async Task<(HttpClient Client, Guid SessionId)> StartAsync(int permits, int? generalPermits = null)
    {
        var host = Api.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var limits = new Dictionary<string, string?>
                {
                    ["Security:Limits:Identify:Permits"] = permits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (generalPermits is { } general)
                {
                    limits["Security:Limits:Default:Permits"] = general.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                configuration.AddInMemoryCollection(limits);
            });
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, PeerFilter>().AddSingleton<IStartupFilter, BreakBodyFilter>());
        });
        _factories.Add(host);
        var library = await CreateLibraryAsync();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, library.UserWithRole(LibraryRole.Owner).ToString());

        var created = await client.PostAsJsonAsync($"/api/v1/libraries/{library.Id}/scan-sessions", new { }, TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (client, (await ReadJsonAsync(created)).GetProperty("id").GetGuid());
    }
}
