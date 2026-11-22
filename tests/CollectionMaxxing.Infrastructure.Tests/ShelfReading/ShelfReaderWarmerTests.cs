using CollectionMaxxing.Infrastructure.ShelfReading;
using CollectionMaxxing.Infrastructure.Tests.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.ShelfReading;

/// <summary>The wake-up call to the shelf reader, against WireMock on a random loopback port. No live server, no model.</summary>
public sealed class ShelfReaderWarmerTests : IDisposable
{
    private const string Path = "/v1/chat/completions";

    private readonly WireMockServer _server = LoopbackWireMock.Start();

    private readonly FakeTimeProvider _time = new();

    private readonly Capture _log = new();

    public void Dispose() => _server.Dispose();

    private ShelfReaderWarmer Warmer(string? endpoint = null)
    {
        var options = new FixedOptions(new ShelfReaderOptions { Endpoint = endpoint ?? _server.Url!, Model = "test-model" });
        return new ShelfReaderWarmer(new PlainClients(), options, _time, _log);
    }

    private sealed class PlainClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class FixedOptions(ShelfReaderOptions value) : IOptionsMonitor<ShelfReaderOptions>
    {
        public ShelfReaderOptions CurrentValue => value;

        public ShelfReaderOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ShelfReaderOptions, string?> listener) => null;
    }

    private int Hits => _server.LogEntries.Count(e => e.RequestMessage?.Path == Path);

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10);
        }

        done().ShouldBeTrue();
    }

    [Fact]
    public async Task Asking_for_a_warm_up_returns_at_once_while_the_reader_is_still_loading()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(2)).WithBody("{}"));
        var warmer = Warmer();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        warmer.RequestWarm();
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        await Until(() => Hits == 1);
    }

    [Fact]
    public async Task The_warm_up_asks_for_one_token_of_text_and_sends_no_picture()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        Warmer().RequestWarm();
        await Until(() => Hits == 1);

        var body = _server.LogEntries.Single().RequestMessage!.Body!;
        body.ShouldContain("\"max_tokens\":1");
        body.ShouldNotContain("image_url");
    }

    [Fact]
    public async Task A_second_request_while_a_warm_up_runs_is_dropped()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(2)).WithBody("{}"));
        var warmer = Warmer();

        warmer.RequestWarm();
        await Task.Delay(300);
        warmer.RequestWarm();
        warmer.RequestWarm();
        await Until(() => Hits >= 1);
        await Task.Delay(300);

        Hits.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_warm_up_is_allowed_once_the_last_one_has_ended()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        var warmer = Warmer();

        warmer.RequestWarm();
        await Until(() => _log.Messages.Count == 1);
        warmer.RequestWarm();
        await Until(() => Hits == 2);
    }

    [Fact]
    public async Task A_reader_that_answers_with_an_error_changes_nothing_and_is_not_thrown()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost()).RespondWith(Response.Create().WithStatusCode(503));

        Warmer().RequestWarm();

        await Until(() => _log.Messages.Count == 1);
        _log.Messages.Single().ShouldContain("503");
    }

    [Fact]
    public async Task A_reader_that_cannot_be_reached_is_logged_by_type_only()
    {
        Warmer("http://127.0.0.1:1").RequestWarm();

        await Until(() => _log.Messages.Count == 1);
        _log.Messages.Single().ShouldContain("HttpRequestException");
        _log.Messages.Single().ShouldNotContain("127.0.0.1");
    }

    [Fact]
    public async Task A_warm_up_the_reader_never_answers_is_cut_off_after_its_own_timeout()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromMinutes(5)).WithBody("{}"));
        var warmer = Warmer();

        warmer.RequestWarm();
        await Task.Delay(300);
        _time.Advance(ShelfReaderWarmer.WarmTimeout + TimeSpan.FromSeconds(1));

        await Until(() => _log.Messages.Count == 1);
        _log.Messages.Single().ShouldContain("Cancel");
    }

    [Fact]
    public async Task With_no_endpoint_nothing_is_called()
    {
        Warmer("").RequestWarm();
        await Task.Delay(200);

        Hits.ShouldBe(0);
    }

    private sealed class Capture : ILogger<ShelfReaderWarmer>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
