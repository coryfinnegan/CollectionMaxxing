using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Infrastructure.ShelfReading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Evals;

/// <summary>
/// Reads the golden photos with the server's own shelf reader for the eval rows (ADR-033). The picture goes through the still maker, then the reader. The
/// answer is kept in <c>reader-cache/&lt;photo sha256&gt;-&lt;settings sha256&gt;.json</c> in <c>Evals:ResultsPath</c> (settings: the model, the prompt and the
/// reader's token, picture-size and repeat limits), so a re-ground never reads a photo twice, while a new model, prompt, setting or picture reads again. A
/// read that failed (unavailable, timed out) is not kept. Reads are taken one at a time: the reader has one slot.
/// </summary>
public sealed class ServerReadings(
    IGoldenSet golden,
    IShelfReader reader,
    IShelfStillMaker stills,
    IOptions<ShelfReaderOptions> reading,
    IOptions<EvalsOptions> evals,
    TimeProvider time,
    ILogger<ServerReadings> logger,
    int readerVersion = ServerReadings.ReaderVersion) : IServerReadings
{
    private const string CacheFolder = "reader-cache";
    private const int MaxPictureBytes = 64 * 1024 * 1024;
    private const int MaxCacheBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Part of the cache key. Bump it whenever the still maker (JPEG quality, orientation, resizing) or the reader's parsing of the model's answer changes, so a
    /// reading made by the older code is not served. The server file llama-server runs is identified by <c>ShelfReader:Model</c> only: a swapped model file needs a new name.
    /// </summary>
    internal const int ReaderVersion = 1;

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public bool IsConfigured => evals.Value.Enabled && Settings() is { } s && !string.IsNullOrWhiteSpace(s.Endpoint);

    public string ReaderLabel => Settings() is { } s
        ? $"model {(string.IsNullOrWhiteSpace(s.Model) ? "(not named)" : s.Model)}, prompt hash {Hex(Sha(s.Prompt))[..12]}"
        : "reader settings not valid";

    public async Task<ServerReading?> ReadAsync(string photoId, bool cachedOnly, CancellationToken cancellationToken)
    {
        if (!IsConfigured || Settings() is not { } settings)
        {
            return null;
        }

        var picture = Load(photoId);
        if (picture is null)
        {
            return null;
        }

        var cachePath = CachePath(picture, settings);
        await OneAtATime.WaitAsync(cancellationToken);
        try
        {
            if (cachePath is not null && Cached(cachePath) is { } cached)
            {
                return cached;
            }

            if (cachedOnly)
            {
                return null;
            }

            var still = stills.Make(picture);
            if (still is null)
            {
                return new ServerReading(ShelfReadResult.Empty(ShelfReadStatus.Unavailable), 0);
            }

            var started = time.GetTimestamp();
            var result = await reader.ReadAsync(still, cancellationToken);
            var elapsed = (long)time.GetElapsedTime(started).TotalMilliseconds;
            if (result.Status == ShelfReadStatus.Off)
            {
                return null;
            }

            var served = new ServerReading(result, elapsed);
            if (cachePath is not null && result.Status is ShelfReadStatus.Ok or ShelfReadStatus.CutShort)
            {
                Keep(cachePath, served);
            }

            return served;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private ShelfReaderOptions? Settings()
    {
        try
        {
            return reading.Value;
        }
        catch (OptionsValidationException)
        {
            return null;
        }
    }

    private byte[]? Load(string photoId)
    {
        try
        {
            if (golden.OpenImage(photoId) is not { } image)
            {
                return null;
            }

            using var content = image.Content;
            if (image.Length > MaxPictureBytes)
            {
                return null;
            }

            using var memory = new MemoryStream((int)image.Length);
            content.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? CachePath(byte[] picture, ShelfReaderOptions settings)
    {
        if (string.IsNullOrWhiteSpace(evals.Value.ResultsPath))
        {
            return null;
        }

        var key = JsonSerializer.Serialize(new { ReaderVersion = readerVersion, settings.Model, settings.Prompt, settings.MaxTokens, settings.MaxLongEdge, settings.MaxRepeats });
        return Path.Combine(Path.GetFullPath(evals.Value.ResultsPath), CacheFolder, Hex(SHA256.HashData(picture)) + "-" + Hex(Sha(key)) + ".json");
    }

    private static ServerReading? Cached(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxCacheBytes)
            {
                return null;
            }

            var entry = JsonSerializer.Deserialize<CacheEntry>(EvalFileIo.ReadAllText(path), Json);
            return entry is { Status: ShelfReadStatus.Ok or ShelfReadStatus.CutShort, Items: not null }
                ? new ServerReading(new ShelfReadResult([.. entry.Items], entry.Status), entry.LatencyMs)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // read again
        }
    }

    private void Keep(string path, ServerReading read)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(new CacheEntry(read.Result.Status, [.. read.Result.Items], read.LatencyMs), Json));
            EvalFileIo.MoveReplace(temp, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only the type is logged: the message names a path.
            logger.LogWarning("A server reading could not be kept ({Failure}); it will be read again next time.", e.GetType().Name);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a stray temp file is harmless
            }
        }
    }

    private static byte[] Sha(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed record CacheEntry(ShelfReadStatus Status, List<ShelfReadItem> Items, long LatencyMs);
}
