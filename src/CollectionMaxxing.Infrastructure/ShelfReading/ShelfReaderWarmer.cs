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

    private int _running;

    public void RequestWarm()
    {
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
        try
        {
            var settings = options.CurrentValue;
            if (string.IsNullOrWhiteSpace(settings.Endpoint) || !ShelfReaderOptions.IsHttpUrl(settings.Endpoint))
            {
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
            var status = (int)response.StatusCode;
            var took = (long)time.GetElapsedTime(started).TotalMilliseconds;
            LogWarm(status, took);
        }
        catch (Exception e)
        {
            var type = e.GetType().Name;
            var took = (long)time.GetElapsedTime(started).TotalMilliseconds;
            LogFailed(type, took);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelf reader warm-up answered HTTP {HttpStatus} in {Milliseconds} ms.")]
    private partial void LogWarm(int httpStatus, long milliseconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelf reader warm-up failed ({ExceptionType}) after {Milliseconds} ms; nothing changed.")]
    private partial void LogFailed(string exceptionType, long milliseconds);
}
