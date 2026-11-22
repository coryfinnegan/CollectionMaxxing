using System.Net.Http.Json;
using CollectionMaxxing.Application.Recognition;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.ShelfReading;

/// <summary>
/// Wakes a sleeping llama-server by asking it for one token of text with no picture: the cheapest request it answers only once the model is in memory
/// (<c>/health</c> answers without loading it). It does not take the read gate, so a real read that arrives during a warm-up is never told the reader is busy
/// because of it; the server queues the two. One warm-up at a time (a call while one runs is dropped), its own timeout, never awaited by the caller,
/// never throws. Logs carry the exception type and timing, nothing else. A singleton.
/// </summary>
public sealed partial class ShelfReaderWarmer(
    IHttpClientFactory clients, IOptionsMonitor<ShelfReaderOptions> options, TimeProvider time, ILogger<ShelfReaderWarmer> log)
    : IShelfReaderWarmer
{
    public const string ClientName = "shelf-reader-warm";

    /// <summary>A cold load took 37 s on the owner's card; leave room for a slower volume.</summary>
    public static readonly TimeSpan WarmTimeout = TimeSpan.FromSeconds(90);

    /// <summary>A warm-up that was answered keeps the reader loaded for five idle minutes; a call within this long of one is ignored, so no caller can keep the card held by calling every few minutes.</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromMinutes(4);

    private int _running;

    private long _lastAnsweredTicks; // UTC ticks of the last warm-up the reader answered with success; 0 when none

    public void RequestWarm()
    {
        var last = Volatile.Read(ref _lastAnsweredTicks);
        if (last != 0 && time.GetUtcNow() - new DateTimeOffset(last, TimeSpan.Zero) < Floor)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        // Task.Run: nothing here, not even reading the settings, may run on or delay the request.
        _ = Task.Run(WarmAsync);
    }

    private async Task WarmAsync()
    {
        var started = time.GetTimestamp();
        var status = 0;
        string? failure = null;
        try
        {
            var settings = options.CurrentValue;
            if (string.IsNullOrWhiteSpace(settings.Endpoint) || !ShelfReaderOptions.IsHttpUrl(settings.Endpoint))
            {
                Volatile.Write(ref _running, 0);
                return;
            }

            using var timeout = new CancellationTokenSource(WarmTimeout, time);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Endpoint.TrimEnd('/') + "/v1/chat/completions"))
            {
                Content = JsonContent.Create(new
                {
                    model = settings.Model,
                    temperature = 0,
                    max_tokens = 1,
                    messages = new[] { new { role = "user", content = "Hi" } },
                }),
            };
            using var http = clients.CreateClient(ClientName);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                Volatile.Write(ref _lastAnsweredTicks, time.GetUtcNow().UtcTicks);
            }
        }
        catch (Exception e)
        {
            failure = e.GetType().Name;
        }

        // Free the place before logging: whoever sees the log may ask again at once.
        Volatile.Write(ref _running, 0);
        var took = (long)time.GetElapsedTime(started).TotalMilliseconds;
        if (failure is null)
        {
            if (status != 0)
            {
                LogWarm(status, took);
            }
        }
        else
        {
            LogFailed(failure, took);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelf reader warm-up answered HTTP {HttpStatus} in {Milliseconds} ms.")]
    private partial void LogWarm(int httpStatus, long milliseconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelf reader warm-up failed ({ExceptionType}) after {Milliseconds} ms; nothing changed.")]
    private partial void LogFailed(string exceptionType, long milliseconds);
}
