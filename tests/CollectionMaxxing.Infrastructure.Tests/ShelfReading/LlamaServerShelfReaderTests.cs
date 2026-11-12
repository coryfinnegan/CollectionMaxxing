using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Infrastructure.ShelfReading;
using CollectionMaxxing.Infrastructure.Tests.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.ShelfReading;

/// <summary>The llama-server client, end to end against WireMock on a random loopback port. No live server, no model.</summary>
public sealed class LlamaServerShelfReaderTests : IDisposable
{
    private const string Path = "/v1/chat/completions";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9];

    private readonly WireMockServer _server = LoopbackWireMock.Start();

    private readonly CapturingLogger _log = new();

    public void Dispose() => _server.Dispose();

    private LlamaServerShelfReader Reader(Action<ShelfReaderOptions>? tweak = null, string? endpoint = null)
    {
        var options = new ShelfReaderOptions { Endpoint = endpoint ?? _server.Url!, Model = "test-model", TimeoutSeconds = 30 };
        tweak?.Invoke(options);
        return new LlamaServerShelfReader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, Options.Create(options), _log);
    }

    private void Reply(string content, string finish = "stop", int status = 200)
    {
        var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finish } } });
        _server.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));
    }

    private const string SecondMatch = "*Look at the photo again*";

    private void ReplySecond(string content, string finish = "stop", int status = 200, TimeSpan? delay = null)
    {
        var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finish } } });
        ReplySecondRaw(body, status, delay);
    }

    private void ReplySecondRaw(string body, int status = 200, TimeSpan? delay = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);
        _server.Given(Request.Create().WithPath(Path).UsingPost().WithBody(new WireMock.Matchers.WildcardMatcher(SecondMatch)))
            .AtPriority(-1)
            .RespondWith(delay is { } d ? response.WithDelay(d) : response);
    }

    private static readonly string FirstAnswer = Items(("Dune", "Frank Herbert", "book"), ("Emma", "Austen", "book"));

    private int SecondRequests => _server.LogEntries.Count(e => e.RequestMessage!.Body!.Contains("Look at the photo again"));

    [Fact]
    public async Task A_second_look_appends_new_items_after_the_first_ones_and_skips_repeated_titles()
    {
        Reply(FirstAnswer);
        ReplySecond(Items((" dune ", "", "book"), ("Hamlet", "Shakespeare", "book"), ("Alien", "", "film")));

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        result.Items.Select(i => i.Title).ShouldBe(["Dune", "Emma", "Hamlet", "Alien"]);
        result.Items[2].Creator.ShouldBe("Shakespeare");
        _server.LogEntries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_reported_time_is_the_sum_of_both_turns()
    {
        ReplySecond("""{"items":[]}""", delay: TimeSpan.FromMilliseconds(700));
        _server.Given(Request.Create().WithPath(Path).UsingPost()).AtPriority(5)
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithDelay(TimeSpan.FromMilliseconds(700))
                .WithBody(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = FirstAnswer }, finish_reason = "stop" } } })));

        await Reader().ReadAsync(Jpeg, default);

        var line = _log.Messages.Single(m => m.StartsWith("Shelf read ended"));
        var ms = int.Parse(line.Split(" in ")[1].Split(' ')[0]);
        ms.ShouldBeGreaterThanOrEqualTo(1400);
    }

    [Fact]
    public async Task An_empty_second_list_changes_nothing()
    {
        Reply(FirstAnswer);
        ReplySecond("""{"items":[]}""");

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        result.Items.Select(i => i.Title).ShouldBe(["Dune", "Emma"]);
        SecondRequests.ShouldBe(1);
    }

    [Fact]
    public async Task The_second_request_carries_the_picture_the_first_answer_as_assistant_and_the_follow_up_with_its_own_token_cap()
    {
        Reply(FirstAnswer);
        ReplySecond("""{"items":[]}""");

        await Reader(o => { o.MaxTokens = 1234; o.SecondLookMaxTokens = 321; }).ReadAsync(Jpeg, default);

        var entries = _server.LogEntries.Select(e => e.RequestMessage!.Body!).ToArray();
        entries.Length.ShouldBe(2);
        using var first = JsonDocument.Parse(entries[0]);
        using var second = JsonDocument.Parse(entries[1]);
        first.RootElement.GetProperty("max_tokens").GetInt32().ShouldBe(1234);
        second.RootElement.GetProperty("max_tokens").GetInt32().ShouldBe(321);
        second.RootElement.GetProperty("temperature").GetDouble().ShouldBe(0);
        second.RootElement.GetProperty("model").GetString().ShouldBe("test-model");
        second.RootElement.GetProperty("response_format").GetRawText().ShouldBe(first.RootElement.GetProperty("response_format").GetRawText());
        var messages = second.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        messages.Length.ShouldBe(3);
        messages[0].GetRawText().ShouldBe(first.RootElement.GetProperty("messages")[0].GetRawText());
        messages[0].GetProperty("content").EnumerateArray().First().GetProperty("image_url").GetProperty("url").GetString()
            .ShouldBe("data:image/jpeg;base64," + Convert.ToBase64String(Jpeg));
        messages[1].GetProperty("role").GetString().ShouldBe("assistant");
        messages[1].GetProperty("content").GetString().ShouldBe(FirstAnswer);
        messages[2].GetProperty("role").GetString().ShouldBe("user");
        messages[2].GetProperty("content").GetString().ShouldBe(
            "Look at the photo again, shelf by shelf, including lower shelves and stacks. List only the items you have not listed yet, in the same format. If you have listed every item you can read, answer with an empty list.");
    }

    [Fact]
    public async Task The_assistant_message_is_the_first_reply_exactly_as_the_server_returned_it()
    {
        const string odd = "  {\"items\":[{\"title\":\"Dune\",\"creator\":\"\",\"kind\":\"book\"}]}\n ";
        Reply(odd);
        ReplySecond("""{"items":[]}""");

        await Reader().ReadAsync(Jpeg, default);

        using var second = JsonDocument.Parse(_server.LogEntries[^1].RequestMessage!.Body!);
        second.RootElement.GetProperty("messages")[1].GetProperty("content").GetString().ShouldBe(odd);
    }

    [Theory]
    [InlineData("cut")]
    [InlineData("unparsable")]
    [InlineData("500")]
    public async Task A_failed_second_look_leaves_the_first_answer_standing_with_status_ok(string kind)
    {
        Reply(FirstAnswer);
        switch (kind)
        {
            case "cut":
                ReplySecond("""{"items":[{"title":"THE C","creator":"","kind":"book"},{"title":"a Co""", "length");
                break;
            case "unparsable":
                ReplySecond("I see more books.");
                break;
            default:
                ReplySecond("oops", status: 500);
                break;
        }

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        result.Items.Select(i => i.Title).ShouldBe(["Dune", "Emma"]);
        SecondRequests.ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{}""")]
    public async Task A_second_look_with_no_usable_choice_leaves_the_first_answer_standing(string body)
    {
        Reply(FirstAnswer);
        ReplySecondRaw(body);

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        result.Items.Select(i => i.Title).ShouldBe(["Dune", "Emma"]);
        SecondRequests.ShouldBe(1);
    }

    [Fact]
    public async Task A_second_look_that_runs_out_of_the_time_budget_leaves_the_first_answer_standing()
    {
        Reply(FirstAnswer);
        ReplySecond(Items(("Hamlet", "", "book")), delay: TimeSpan.FromSeconds(15));

        var result = await Reader(o => o.TimeoutSeconds = 11).ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        result.Items.Select(i => i.Title).ShouldBe(["Dune", "Emma"]);
    }

    [Fact]
    public async Task With_less_than_the_floor_left_after_the_first_turn_there_is_no_second_look()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = FirstAnswer }, finish_reason = "stop" } } }))
                .WithDelay(TimeSpan.FromSeconds(4)));

        var result = await Reader(o => o.TimeoutSeconds = 12).ReadAsync(Jpeg, default);
        await Task.Delay(TimeSpan.FromSeconds(1));

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        _server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelling_during_the_second_look_propagates()
    {
        Reply(FirstAnswer);
        ReplySecond(Items(("Hamlet", "", "book")), delay: TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var read = Reader().ReadAsync(Jpeg, cts.Token);
        await Task.Delay(1500);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => read);
    }

    [Theory]
    [InlineData("cut")]
    [InlineData("empty")]
    [InlineData("failed")]
    [InlineData("repeats")]
    [InlineData("unclosed")]
    [InlineData("off")]
    [InlineData("noroom")]
    public async Task No_second_look_when_the_first_answer_is_not_good_or_the_setting_or_time_is_missing(string kind)
    {
        switch (kind)
        {
            case "cut":
                Reply("""{"items":[{"title":"Emma","creator":"","kind":"book"},{"title":"Ali""", finish: "length");
                break;
            case "empty":
                Reply("""{"items":[]}""");
                break;
            case "failed":
                Reply("oops", status: 500);
                break;
            case "repeats":
                Reply(Items(("Dune", "", "book"), ("Dune", "", "book"), ("Dune", "", "book"), ("Emma", "", "book")));
                break;
            case "unclosed":
                Reply("""{"items":[{"title":"Emma","creator":"","kind":"book"},{"title":"Ali""", finish: "stop");
                break;
            default:
                Reply(FirstAnswer);
                break;
        }

        await Reader(o =>
        {
            o.SecondLook = kind != "off";
            if (kind == "noroom")
            {
                o.TimeoutSeconds = 9;
            }
        }).ReadAsync(Jpeg, default);

        _server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_second_look_logs_no_titles()
    {
        Reply(Items(("Secret Title", "", "book")));
        ReplySecond(Items(("Secret Other", "", "book")));

        await Reader().ReadAsync(Jpeg, default);

        _log.Messages.Count.ShouldBe(2);
        _log.Messages.ShouldAllBe(m => !m.Contains("Secret"));
    }

    private static string Items(params (string Title, string Creator, string Kind)[] items) =>
        JsonSerializer.Serialize(new { items = items.Select(i => new { title = i.Title, creator = i.Creator, kind = i.Kind }) });

    [Fact]
    public async Task With_no_endpoint_the_reader_is_off_and_calls_nothing()
    {
        Reply(Items(("A", "", "book")));

        var result = await Reader(endpoint: "").ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Off);
        result.Items.ShouldBeEmpty();
        _server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_posts_one_chat_completion_with_the_picture_prompt_model_schema_and_token_cap()
    {
        Reply(Items(("Dune", "Frank Herbert", "book")));

        var result = await Reader(o => { o.MaxTokens = 1234; o.SecondLook = false; }).ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        var request = _server.LogEntries.Single().RequestMessage!;
        request.Path.ShouldBe(Path);
        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        root.GetProperty("model").GetString().ShouldBe("test-model");
        root.GetProperty("temperature").GetDouble().ShouldBe(0);
        root.GetProperty("max_tokens").GetInt32().ShouldBe(1234);
        var content = root.GetProperty("messages").EnumerateArray().Single().GetProperty("content").EnumerateArray().ToArray();
        var image = content.Single(c => c.GetProperty("type").GetString() == "image_url");
        image.GetProperty("image_url").GetProperty("url").GetString()
            .ShouldBe("data:image/jpeg;base64," + Convert.ToBase64String(Jpeg));
        content.Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()
            .ShouldBe(ShelfReaderOptions.DefaultPrompt);
        var format = root.GetProperty("response_format");
        format.GetProperty("type").GetString().ShouldBe("json_schema");
        var schema = format.GetProperty("json_schema").GetProperty("schema");
        var item = schema.GetProperty("properties").GetProperty("items").GetProperty("items");
        item.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(k => k.GetString())
            .ShouldBe(["book", "film", "tv", "game"]);
        item.GetProperty("required").EnumerateArray().Select(k => k.GetString()).ShouldBe(["title", "creator", "kind"], ignoreOrder: true);
    }

    [Fact]
    public void The_default_prompt_is_the_spikes_prompt()
    {
        ShelfReaderOptions.DefaultPrompt.ShouldStartWith("This is a photo of a shelf of books, video tapes, discs or games.");
        ShelfReaderOptions.DefaultPrompt.ShouldEndWith("repeat an entry if there are two copies.");
    }

    [Fact]
    public async Task Items_come_back_in_order_with_unknown_kinds_as_none_blank_titles_dropped_and_text_trimmed()
    {
        Reply(Items(("  Dune ", " Frank Herbert  ", "book"), ("   ", "Nobody", "book"), ("Alien", "", "film"), ("Mystery", "", "podcast"), ("Halo", "", "GAME"), ("Lost", "", "tv")));

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        result.Items.ShouldBe([
            new ShelfReadItem("Dune", "Frank Herbert", ShelfItemKind.Book),
            new ShelfReadItem("Alien", "", ShelfItemKind.Film),
            new ShelfReadItem("Mystery", "", ShelfItemKind.None),
            new ShelfReadItem("Halo", "", ShelfItemKind.Game),
            new ShelfReadItem("Lost", "", ShelfItemKind.Tv),
        ]);
    }

    [Fact]
    public async Task A_title_repeated_more_than_the_limit_in_a_row_is_kept_only_up_to_the_limit()
    {
        Reply(Items(("Dune", "", "book"), ("Dune", "", "book"), ("dune", "", "book"), ("Dune", "", "book"), ("Emma", "", "book"), ("Dune", "", "book")));

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Items.Select(i => i.Title).ShouldBe(["Dune", "Dune", "Emma", "Dune"]);
        result.Status.ShouldBe(ShelfReadStatus.Ok);
    }

    [Fact]
    public async Task A_read_stopped_at_the_token_cap_keeps_complete_items_cuts_the_repeated_tail_to_one_and_says_cut_short()
    {
        const string cut = """{"items":[{"title":"Emma","creator":"Austen","kind":"book"},{"title":"Dune","creator":"","kind":"book"},{"title":"Dune","creator":"","kind":"book"},{"title":"Dune","creator":"","kind":"book"},{"title":"Du""";
        Reply(cut, finish: "length");

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.CutShort);
        result.Items.Select(i => i.Title).ShouldBe(["Emma", "Dune"]);
    }

    [Fact]
    public async Task A_cut_short_read_with_no_repeats_keeps_every_complete_item()
    {
        Reply("""{"items":[{"title":"Emma","creator":"","kind":"book"},{"title":"Ali""", finish: "length");

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.CutShort);
        result.Items.Select(i => i.Title).ShouldBe(["Emma"]);
    }

    [Fact]
    public async Task A_slow_read_is_a_timeout_and_is_not_retried()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(4)));

        var result = await Reader(o => o.TimeoutSeconds = 1).ReadAsync(Jpeg, default);
        await Task.Delay(TimeSpan.FromSeconds(4)); // WireMock logs a request when its delayed reply ends

        result.Status.ShouldBe(ShelfReadStatus.TimedOut);
        result.Items.ShouldBeEmpty();
        _server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_server_that_is_down_is_unavailable_and_does_not_throw()
    {
        var result = await Reader(endpoint: $"http://127.0.0.1:{UnusedPort()}").ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Unavailable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_server_error_is_unavailable_and_is_not_retried()
    {
        Reply("oops", status: 500);

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Unavailable);
        _server.LogEntries.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("I can see some books on a shelf.")]
    [InlineData("""{"items":[{"title":"Emma","crea""")]
    [InlineData("""{"other":1}""")]
    public async Task A_reply_that_is_not_the_schema_is_an_empty_read_marked_unavailable(string content)
    {
        Reply(content);

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Unavailable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_items_list_is_a_good_empty_read()
    {
        Reply("""{"items":[]}""");

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Ok);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(4)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => Reader().ReadAsync(Jpeg, cts.Token));
    }

    [Fact]
    public async Task Logs_carry_no_titles_and_no_picture_bytes()
    {
        Reply(Items(("Secret Title", "Secret Author", "book")));

        await Reader().ReadAsync(Jpeg, default);
        _server.Reset();
        Reply("not json at all Secret Title");
        await Reader().ReadAsync(Jpeg, default);

        _log.Messages.ShouldNotBeEmpty();
        _log.Messages.ShouldAllBe(m => !m.Contains("Secret") && !m.Contains(Convert.ToBase64String(Jpeg)));
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop"}]}""")]
    [InlineData("""[1,2]""")]
    public async Task A_reply_with_no_usable_choice_is_unavailable_not_an_exception(string body)
    {
        _server.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Unavailable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("localhost:8081")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("/relative/path")]
    public async Task A_malformed_endpoint_is_unavailable_with_a_log_line_that_does_not_print_it(string endpoint)
    {
        var result = await Reader(endpoint: endpoint).ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Unavailable);
        _log.Messages.ShouldNotBeEmpty();
        _log.Messages.ShouldAllBe(m => !m.Contains(endpoint));
    }

    [Fact]
    public async Task Invalid_options_make_a_read_unavailable_not_an_exception()
    {
        var invalid = new LlamaServerShelfReader(new HttpClient(), new ThrowingOptions(), _log);

        var result = await invalid.ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.Unavailable);
    }

    [Fact]
    public async Task A_list_that_never_closes_without_the_token_cap_is_cut_short_and_its_repeated_tail_is_cut_to_one()
    {
        Reply("""{"items":[{"title":"Emma","creator":"","kind":"book"},{"title":"Dune","creator":"","kind":"book"},{"title":"Dune","creator":"","kind":"book"},{"title":"Du""", finish: "stop");

        var result = await Reader().ReadAsync(Jpeg, default);

        result.Status.ShouldBe(ShelfReadStatus.CutShort);
        result.Items.Select(i => i.Title).ShouldBe(["Emma", "Dune"]);
    }

    private sealed class ThrowingOptions : IOptions<ShelfReaderOptions>
    {
        public ShelfReaderOptions Value => throw new OptionsValidationException("", typeof(ShelfReaderOptions), ["MaxTokens must be above 0."]);
    }

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class CapturingLogger : ILogger<LlamaServerShelfReader>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception);
    }
}
