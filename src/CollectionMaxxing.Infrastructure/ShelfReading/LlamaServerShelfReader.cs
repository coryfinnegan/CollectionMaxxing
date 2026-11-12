using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Application.ShelfReading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.ShelfReading;

/// <summary>
/// Reads a shelf picture through any OpenAI-compatible server (llama.cpp's llama-server): one chat completion and, when the first answer ended normally, one second look for items it left out; no retry (a
/// retry would double a 10 s GPU call). Logs carry counts, status and timing, never titles or picture bytes.
/// </summary>
public sealed partial class LlamaServerShelfReader(HttpClient http, IOptions<ShelfReaderOptions> options, ILogger<LlamaServerShelfReader> log)
    : IShelfReader
{
    private static readonly object ItemsSchema = new
    {
        type = "object",
        properties = new
        {
            items = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        creator = new { type = "string" },
                        kind = new { type = "string", @enum = new[] { "book", "film", "tv", "game" } },
                    },
                    required = new[] { "title", "creator", "kind" },
                },
            },
        },
        required = new[] { "items" },
    };

    /// <summary>The second turn, word for word.</summary>
    internal const string SecondLookPrompt =
        "Look at the photo again, shelf by shelf, including lower shelves and stacks. List only the items you have not listed yet, in the same format. "
        + "If you have listed every item you can read, answer with an empty list.";

    /// <summary>A second turn is skipped when less time than this is left inside <c>TimeoutSeconds</c>. A test seam, not configuration (the Infrastructure project exposes no internals to its tests).</summary>
    public TimeSpan SecondLookFloor { get; init; } = TimeSpan.FromSeconds(DefaultSecondLookFloorSeconds);

    public const int DefaultSecondLookFloorSeconds = 10;

    public async Task<ShelfReadResult> ReadAsync(byte[] jpeg, CancellationToken cancellationToken)
    {
        ShelfReaderOptions settings;
        try
        {
            settings = options.Value;
        }
        catch (OptionsValidationException)
        {
            LogBadSettings(); // the failures name settings, not values; they are left out of the log anyway
            return ShelfReadResult.Empty(ShelfReadStatus.Unavailable);
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            return ShelfReadResult.Empty(ShelfReadStatus.Off);
        }

        if (!ShelfReaderOptions.IsHttpUrl(settings.Endpoint))
        {
            LogBadSettings();
            return ShelfReadResult.Empty(ShelfReadStatus.Unavailable);
        }

        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));
        try
        {
            var firstMessage = FirstMessage(jpeg, settings);
            var first = await SendAsync(settings, [firstMessage], settings.MaxTokens, timeout.Token);
            var result = first.Result;
            if (settings.SecondLook && first.Content is not null && result.Status == ShelfReadStatus.Ok && result.Items.Count > 0 && !first.Degenerate
                && TimeSpan.FromSeconds(settings.TimeoutSeconds) - clock.Elapsed >= SecondLookFloor)
            {
                result = await SecondLookAsync(settings, firstMessage, first.Content, result, clock, cancellationToken, timeout.Token);
            }

            LogRead(result.Status, result.Items.Count, (int)clock.ElapsedMilliseconds, first.HttpStatus);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LogRead(ShelfReadStatus.TimedOut, 0, (int)clock.ElapsedMilliseconds, 0);
            return ShelfReadResult.Empty(ShelfReadStatus.TimedOut);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            LogRead(ShelfReadStatus.Unavailable, 0, (int)clock.ElapsedMilliseconds, 0);
            return ShelfReadResult.Empty(ShelfReadStatus.Unavailable);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelf read ended {Status} with {Count} items in {Milliseconds} ms (HTTP {HttpStatus}).")]
    private partial void LogRead(ShelfReadStatus status, int count, int milliseconds, int httpStatus);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shelf reader settings are not valid (ShelfReader: Endpoint must be an http or https address; MaxTokens, MaxLongEdge, TimeoutSeconds, MaxRepeats and SecondLookMaxTokens above 0), so nothing was read.")]
    private partial void LogBadSettings();

    /// <summary>The picture and prompt. Built once and sent unchanged in both turns, so llama-server reuses its cached prefix.</summary>
    private static object FirstMessage(byte[] jpeg, ShelfReaderOptions settings) => new
    {
        role = "user",
        content = new object[]
        {
            new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) } },
            new { type = "text", text = settings.Prompt },
        },
    };

    private static object Body(ShelfReaderOptions settings, object[] messages, int maxTokens) => new
    {
        model = settings.Model,
        temperature = 0,
        max_tokens = maxTokens,
        messages,
        response_format = new { type = "json_schema", json_schema = new { name = "shelf_items", strict = true, schema = ItemsSchema } },
    };

    /// <summary>One turn: what it read, the model's words exactly (null when none), whether it was not a clean answer (cut short, or the repeat rule dropped or cut items: no second look after it, and a second turn like it is not accepted), and the HTTP status.</summary>
    private sealed record Turn(ShelfReadResult Result, string? Content, bool Degenerate, int HttpStatus);

    /// <summary>One chat completion. A server error comes back as an Unavailable result; cancellation and timeouts throw.</summary>
    private async Task<Turn> SendAsync(ShelfReaderOptions settings, object[] messages, int maxTokens, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Endpoint.TrimEnd('/') + "/v1/chat/completions"))
        {
            Content = JsonContent.Create(Body(settings, messages, maxTokens)),
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return new Turn(ShelfReadResult.Empty(ShelfReadStatus.Unavailable), null, false, status);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var (result, content, degenerate) = Interpret(doc.RootElement, settings.MaxRepeats);
        return new Turn(result, content, degenerate, status);
    }

    /// <summary>
    /// Asks once more for items the first answer left out. A second answer that does not end normally, does not parse, errors or runs out of time is dropped whole
    /// and the first answer stands; only the caller's own cancellation propagates.
    /// </summary>
    private async Task<ShelfReadResult> SecondLookAsync(
        ShelfReaderOptions settings, object firstMessage, string firstContent, ShelfReadResult first, Stopwatch clock, CancellationToken caller, CancellationToken budget)
    {
        var before = (int)clock.ElapsedMilliseconds;
        object[] messages =
        [
            firstMessage,
            new { role = "assistant", content = firstContent },
            new { role = "user", content = SecondLookPrompt },
        ];
        var outcome = ShelfReadStatus.Unavailable;
        try
        {
            var second = await SendAsync(settings, messages, settings.SecondLookMaxTokens, budget);
            outcome = second.Result.Status;
            if (second.Result.Status == ShelfReadStatus.Ok && !second.Degenerate)
            {
                var seen = first.Items.Select(i => i.Title.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var extra = second.Result.Items.Where(i => !seen.Contains(i.Title.Trim())).ToList();
                LogSecondLook(outcome, extra.Count, (int)clock.ElapsedMilliseconds - before);
                return extra.Count == 0 ? first : new ShelfReadResult([.. first.Items, .. extra], ShelfReadStatus.Ok);
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = ShelfReadStatus.TimedOut;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            outcome = ShelfReadStatus.Unavailable;
        }

        LogSecondLook(outcome, 0, (int)clock.ElapsedMilliseconds - before);
        return first;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelf second look ended {Status} and added {Count} items in {Milliseconds} ms.")]
    private partial void LogSecondLook(ShelfReadStatus status, int count, int milliseconds);

    private static (ShelfReadResult Result, string? Content, bool Degenerate) Interpret(JsonElement reply, int maxRepeats)
    {
        var none = (ShelfReadResult.Empty(ShelfReadStatus.Unavailable), (string?)null, false);
        var choices = reply.GetProperty("choices");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return none;
        }

        var choice = choices[0];
        var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
        var cut = string.Equals(finish, "length", StringComparison.Ordinal);

        var scan = ItemScanner.Scan(content);
        if (!scan.SawItemsArray)
        {
            return none;
        }

        var limited = LimitRepeats(scan.Items, Math.Max(1, maxRepeats));
        var tripped = limited.Count < scan.Items.Count;
        if (cut)
        {
            return (new ShelfReadResult(DropRepeatedTail(limited), ShelfReadStatus.CutShort), content, true);
        }

        if (!scan.ArrayClosed)
        {
            // Stopped without reaching the token cap and the list never closed: usable items are kept, none means a broken reply.
            return limited.Count == 0
                ? none
                : (new ShelfReadResult(DropRepeatedTail(limited), ShelfReadStatus.CutShort), content, true);
        }

        return (new ShelfReadResult(limited, ShelfReadStatus.Ok), content, tripped);
    }

    private static bool SameTitle(ShelfReadItem a, ShelfReadItem b) => string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);

    private static List<ShelfReadItem> LimitRepeats(List<ShelfReadItem> items, int max)
    {
        var kept = new List<ShelfReadItem>(items.Count);
        var run = 0;
        foreach (var item in items)
        {
            run = kept.Count > 0 && SameTitle(kept[^1], item) ? run + 1 : 1;
            if (run <= max)
            {
                kept.Add(item);
            }
            else
            {
                run = max; // the dropped copy does not start a new run
            }
        }

        return kept;
    }

    /// <summary>A read that hit the token cap often ends in one title said over and over: keep that run once.</summary>
    private static List<ShelfReadItem> DropRepeatedTail(List<ShelfReadItem> items)
    {
        if (items.Count < 2)
        {
            return items;
        }

        var start = items.Count - 1;
        while (start > 0 && SameTitle(items[start - 1], items[^1]))
        {
            start--;
        }

        if (start < items.Count - 1)
        {
            items.RemoveRange(start + 1, items.Count - start - 1);
        }

        return items;
    }
}
