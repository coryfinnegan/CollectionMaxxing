using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The two engine rows the server makes by itself (ADR-033), outside the gate. <see cref="Model"/> is what the server's own shelf reader read in a golden
/// photo, grounded as the phone's model items are; <see cref="Vision"/> is <see cref="EvalCombination"/>'s rule applied to that row and the
/// <c>vision_pipeline</c> row. Neither is judged by the regression check, counted in the scored photos, or used for a draft truth file.
/// </summary>
public static class ServerEngines
{
    public const string Model = "server_model";
    public const string Vision = "server_vision";

    /// <summary>True for a row the server read itself.</summary>
    public static bool Is(string engine) => engine is Model or Vision;
}

/// <summary>Whether grounding a photo also asks for the server's own reading of it.</summary>
public enum ServerReadMode
{
    /// <summary>No server row (the one-shot upload, which runs inside a request).</summary>
    None,

    /// <summary>A reading already kept for this photo and these settings, else no row; the reader and the still maker are never called (a live phone run).</summary>
    CachedOnly,

    /// <summary>A kept reading, else a read (the re-ground).</summary>
    Read,
}

/// <summary>One reading of a golden photo: what the reader said and how long it took (the reader's own time; a cached reading keeps the time it took).</summary>
public sealed record ServerReading(ShelfReadResult Result, long LatencyMs);

/// <summary>
/// The server reading a golden photo with its own shelf reader. Reads happen one at a time, and a photo already read with the same reader settings is
/// answered from disk. Failing to read is a status, never an exception; only cancellation by the caller propagates.
/// </summary>
public interface IServerReadings
{
    /// <summary>True when the evals are on and a reader is set up. When false no server row is made.</summary>
    bool IsConfigured { get; }

    /// <summary>The model and the prompt's hash the reader is set to, for the report.</summary>
    string ReaderLabel { get; }

    /// <summary>
    /// The reading of the golden photo, or null when there is nothing to read (no reader, the photo cannot be opened, or <paramref name="cachedOnly"/> and none is kept).
    /// With <paramref name="cachedOnly"/> the reader and the still maker are never called.
    /// </summary>
    Task<ServerReading?> ReadAsync(string photoId, bool cachedOnly, CancellationToken cancellationToken);
}

/// <summary>Shapes one reading as the phone's model would have uploaded it, so it is grounded by the very same code.</summary>
public static class ServerReadInput
{
    /// <summary>
    /// A reader item carries a title, a creator and a kind, and claims T1 as the phone's confident model reads do; the scorer demotes anything the
    /// catalogue does not match. A read cut short counts as a timeout with its items kept; a failed read is an error row with none.
    /// </summary>
    public static EvalPhotoInput Of(string photoId, ServerReading reading)
    {
        var status = reading.Result.Status;
        var ok = status is ShelfReadStatus.Ok or ShelfReadStatus.CutShort;
        return new EvalPhotoInput(
            photoId,
            ScanEngine.AppleModel, // the grounding is the model engine's; the row is labelled server_model afterwards
            ok ? EvalOutcome.Ok : EvalOutcome.Error,
            null,
            ok ? [.. reading.Result.Items.Select(Item)] : [],
            0,
            reading.LatencyMs,
            null,
            ThermalState.Nominal,
            0,
            TimedOut: status == ShelfReadStatus.CutShort ? true : null);
    }

    private static EvalReadItemInput Item(ShelfReadItem item) => new(ReadOf(item), ResolvedStep.Model, ScanTier.T1);

    /// <summary>
    /// What the reader's item says as a read: its title, its creator when printed and its kind, with no box. The server's own scan
    /// endpoint turns the same items into reads with this, so the eval row and the app ground the very same read.
    /// </summary>
    public static IdentifyItemInput ReadOf(ShelfReadItem item) => new(
        null, null, null, item.Title, string.IsNullOrWhiteSpace(item.Creator) ? null : item.Creator, null, null, KindOf(item.Kind), null, null, null, null,
        ServerRead: true);

    private static WorkKind? KindOf(ShelfItemKind kind) => kind switch
    {
        ShelfItemKind.Book => WorkKind.Book,
        ShelfItemKind.Film => WorkKind.Film,
        ShelfItemKind.Tv => WorkKind.Tv,
        ShelfItemKind.Game => WorkKind.Game,
        _ => null,
    };
}
