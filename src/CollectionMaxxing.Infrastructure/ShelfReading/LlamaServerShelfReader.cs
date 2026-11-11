using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Application.ShelfReading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.ShelfReading;

/// <summary>
/// Reads a shelf picture through any OpenAI-compatible server (llama.cpp's llama-server): one chat completion, no retry (a
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
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Endpoint.TrimEnd('/') + "/v1/chat/completions"))
            {
                Content = JsonContent.Create(Body(jpeg, settings)),
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogRead(ShelfReadStatus.Unavailable, 0, (int)clock.ElapsedMilliseconds, (int)response.StatusCode);
                return ShelfReadResult.Empty(ShelfReadStatus.Unavailable);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var result = Interpret(doc.RootElement, settings.MaxRepeats);
            LogRead(result.Status, result.Items.Count, (int)clock.ElapsedMilliseconds, (int)response.StatusCode);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shelf reader settings are not valid (ShelfReader: Endpoint must be an http or https address; MaxTokens, MaxLongEdge, TimeoutSeconds and MaxRepeats above 0), so nothing was read.")]
    private partial void LogBadSettings();

    private static object Body(byte[] jpeg, ShelfReaderOptions settings) => new
    {
        model = settings.Model,
        temperature = 0,
        max_tokens = settings.MaxTokens,
        messages = new[]
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) } },
                    new { type = "text", text = settings.Prompt },
                },
            },
        },
        response_format = new { type = "json_schema", json_schema = new { name = "shelf_items", strict = true, schema = ItemsSchema } },
    };

    private static ShelfReadResult Interpret(JsonElement reply, int maxRepeats)
    {
        var choices = reply.GetProperty("choices");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return ShelfReadResult.Empty(ShelfReadStatus.Unavailable);
        }

        var choice = choices[0];
        var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
        var cut = string.Equals(finish, "length", StringComparison.Ordinal);

        var scan = ItemScanner.Scan(content);
        if (!scan.SawItemsArray)
        {
            return ShelfReadResult.Empty(ShelfReadStatus.Unavailable);
        }

        var items = LimitRepeats(scan.Items, Math.Max(1, maxRepeats));
        if (cut)
        {
            items = DropRepeatedTail(items);
            return new ShelfReadResult(items, ShelfReadStatus.CutShort);
        }

        if (!scan.ArrayClosed)
        {
            // Stopped without reaching the token cap and the list never closed: usable items are kept, none means a broken reply.
            return items.Count == 0
                ? ShelfReadResult.Empty(ShelfReadStatus.Unavailable)
                : new ShelfReadResult(DropRepeatedTail(items), ShelfReadStatus.CutShort);
        }

        return new ShelfReadResult(items, ShelfReadStatus.Ok);
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
