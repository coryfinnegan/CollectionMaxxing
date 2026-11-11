namespace CollectionMaxxing.Application.ShelfReading;

/// <summary>What kind of thing a shelf item is. <see cref="None"/> when the reader gave no kind, or one we do not know.</summary>
public enum ShelfItemKind
{
    None,
    Book,
    Film,
    Tv,
    Game,
}

/// <summary>How a read ended. Only <see cref="Ok"/> and <see cref="CutShort"/> can carry items.</summary>
public enum ShelfReadStatus
{
    Ok,
    CutShort,
    TimedOut,
    Unavailable,
    Off,
}

/// <summary>One item the reader saw. <see cref="Creator"/> is empty when none is printed.</summary>
public sealed record ShelfReadItem(string Title, string Creator, ShelfItemKind Kind);

/// <summary>The items a picture showed, in the order read, and how the read ended.</summary>
public sealed record ShelfReadResult(IReadOnlyList<ShelfReadItem> Items, ShelfReadStatus Status)
{
    public static ShelfReadResult Empty(ShelfReadStatus status) => new([], status);
}

/// <summary>
/// Reads one shelf picture with a vision-language model (ADR-033). Failing to read is a status, never an exception;
/// only cancellation by the caller propagates.
/// </summary>
public interface IShelfReader
{
    Task<ShelfReadResult> ReadAsync(byte[] jpeg, CancellationToken cancellationToken);
}

/// <summary>Turns any photo (HEIC or JPEG) into an upright, metadata-free JPEG small enough to send to the reader.</summary>
public interface IShelfStillMaker
{
    /// <summary>The JPEG, or null when the bytes are not a picture.</summary>
    byte[]? Make(byte[] picture);
}
