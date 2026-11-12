using System.Text.Json;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Infrastructure.ShelfReading;
using CollectionMaxxing.Infrastructure.Tests.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.ShelfReading;

/// <summary>
/// The reader composed the way the app composes it: ServiceDefaults gives every HttpClient the standard resilience handler
/// (<c>ConfigureHttpClientDefaults</c>), which would retry the reader and cut it off. The default handler here is tuned short
/// (1 s attempt, 3 s total, 10 ms retry delay) so a leak shows up in seconds instead of 30 s; the reader's own timeout is 30 s.
/// </summary>
public sealed class ShelfReaderComposedTests : IDisposable
{
    private const string Path = "/v1/chat/completions";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xD9];

    private readonly WireMockServer _server = LoopbackWireMock.Start();

    public void Dispose() => _server.Dispose();

    private ServiceProvider Build(Dictionary<string, string?>? extra = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["ShelfReader:Endpoint"] = _server.Url,
            ["ShelfReader:TimeoutSeconds"] = "30",
            ["ShelfReader:SecondLook"] = "false",
        };
        foreach (var (key, value) in extra ?? [])
        {
            config[key] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(3);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(2);
            o.Retry.Delay = TimeSpan.FromMilliseconds(10);
        }));
        services.AddShelfReader();
        return services.BuildServiceProvider();
    }

    private void Reply(string content, int status = 200, TimeSpan? delay = null)
    {
        var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } });
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);
        _server.Given(Request.Create().WithPath(Path).UsingPost()).RespondWith(delay is { } wait ? response.WithDelay(wait) : response);
    }

    [Fact]
    public async Task With_the_app_wide_resilience_defaults_a_server_error_is_one_request_and_unavailable()
    {
        Reply("oops", status: 500);
        await using var provider = Build();

        var result = await provider.GetRequiredService<IShelfReader>().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Unavailable);
        _server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task With_the_app_wide_resilience_defaults_a_reply_slower_than_their_attempt_timeout_still_comes_back_ok_once()
    {
        Reply("""{"items":[{"title":"Dune","creator":"","kind":"book"}]}""", delay: TimeSpan.FromSeconds(2));
        await using var provider = Build();

        var result = await provider.GetRequiredService<IShelfReader>().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        result.Items.Count.ShouldBe(1);
        _server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_clients_own_default_timeout_cannot_cut_a_read_short_of_the_reader_timeout()
    {
        // HttpClient.Timeout defaults to 100 s and the reader's TimeoutSeconds defaults to 120: the reader's linked timeout must rule.
        await using var provider = Build();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        factory.CreateClient(nameof(IShelfReader)).Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }
}
