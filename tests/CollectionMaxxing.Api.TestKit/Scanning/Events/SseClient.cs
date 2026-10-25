using System.Text.Json;

namespace CollectionMaxxing.Api.Tests.Scanning.Events;

/// <summary>One server-sent event as the client read it. A comment line (the heartbeat) has the name ":".</summary>
public sealed record SseEvent(string Name, string? Id, JsonElement? Data, string Raw)
{
    public bool IsComment => Name == ":";

    public int Seq => Data!.Value.GetProperty("seq").GetInt32();
}

/// <summary>A minimal SSE reader for tests: connects without waiting for the (endless) body, and reads one event at a time.</summary>
public sealed class SseClient : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private SseClient(HttpResponseMessage response, StreamReader reader)
    {
        _response = response;
        _reader = reader;
    }

    public System.Net.HttpStatusCode StatusCode => _response.StatusCode;

    public string? ContentType => _response.Content.Headers.ContentType?.MediaType;

    public static async Task<SseClient> ConnectAsync(HttpClient client, string url, string? lastEventId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (lastEventId is not null)
        {
            request.Headers.Add("Last-Event-ID", lastEventId);
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        return new SseClient(response, reader);
    }

    /// <summary>The next event, a comment, or null when the server ended the stream. Fails the test if nothing comes in time.</summary>
    public async Task<SseEvent?> NextAsync(TimeSpan? timeout = null)
    {
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        string? name = null;
        string? id = null;
        string? data = null;
        var raw = new List<string>();
        while (true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(cancel.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("No server-sent event arrived in time.");
            }

            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                if (raw.Count == 0)
                {
                    continue;
                }

                return new SseEvent(name ?? ":", id, data is null ? null : JsonDocument.Parse(data).RootElement.Clone(), string.Join('\n', raw));
            }

            raw.Add(line);
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line[7..];
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = line[4..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
        }
    }

    /// <summary>Reads events (skipping comments) until one named <paramref name="name"/> arrives, and returns all read so far.</summary>
    public async Task<List<SseEvent>> ReadUntilAsync(string name, Func<SseEvent, bool>? where = null)
    {
        var read = new List<SseEvent>();
        while (await NextAsync() is { } next)
        {
            if (next.IsComment)
            {
                continue;
            }

            read.Add(next);
            if (next.Name == name && (where?.Invoke(next) ?? true))
            {
                return read;
            }
        }

        throw new InvalidOperationException($"The stream ended before a '{name}' event. Read: {string.Join(", ", read.Select(e => e.Name))}");
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}
