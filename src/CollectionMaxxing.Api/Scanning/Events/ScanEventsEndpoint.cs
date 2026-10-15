using System.Diagnostics;
using System.Text.Json;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Scanning.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Scanning.Events;

/// <summary>
/// <c>GET /scan-sessions/{id}/events</c>: server-sent events for one scan session (docs/04, "Example: SSE event"). The
/// route's filters have already checked the caller owns the session. The <c>id:</c> of an event is the highest candidate
/// <c>seq</c> shown so far, so a client that reconnects with <c>Last-Event-ID</c> gets only what it missed. A comment line
/// goes out every heartbeat; the stream ends when the session does (committed or abandoned).
/// </summary>
public static class ScanEventsEndpoint
{
    public static async Task<IResult> StreamAsync(
        Guid id,
        [FromHeader(Name = "Last-Event-ID")] string? lastEventId,
        HttpContext http,
        ScanEventFeed feed,
        ScanEventOptions options,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken)
    {
        // A bad or missing Last-Event-ID is a fresh start, never an error: EventSource sends whatever it last saw.
        var after = int.TryParse(lastEventId, out var parsed) && parsed > 0 ? parsed : 0;
        if (await feed.OpenAsync(id, after, cancellationToken) is not { } stream)
        {
            return ProblemResults.Problem(StatusCodes.Status404NotFound, "Not found", "That scan session doesn't exist.");
        }

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(cancellationToken);

        var serializer = json.Value.SerializerOptions;
        var quiet = Stopwatch.StartNew();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = await stream.NextAsync(cancellationToken);
                foreach (var item in batch.Events)
                {
                    await WriteAsync(response, item, serializer, cancellationToken);
                    quiet.Restart();
                }

                if (batch.SessionEnded)
                {
                    break;
                }

                if (quiet.Elapsed >= options.Heartbeat)
                {
                    await response.WriteAsync(": keep-alive\n\n", cancellationToken);
                    await response.Body.FlushAsync(cancellationToken);
                    quiet.Restart();
                }

                await Task.Delay(options.PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away, or the host is stopping: the stream just ends.
        }

        return Results.Empty;
    }

    private static async Task WriteAsync(HttpResponse response, ScanEvent item, JsonSerializerOptions serializer, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(item.Data, item.Data.GetType(), serializer);
        var id = item.Id > 0 ? $"id: {item.Id}\n" : string.Empty;
        await response.WriteAsync($"event: {item.Name}\n{id}data: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
