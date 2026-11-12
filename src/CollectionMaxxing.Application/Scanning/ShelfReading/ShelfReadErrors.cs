using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Scanning.ShelfReading;

/// <summary>What the shelf-read endpoint answers when it cannot add anything. <see cref="ReaderBusy"/> and <see cref="ReaderUnavailable"/> are 503s (the endpoint maps them); the rest follow <see cref="ErrorKind"/>.</summary>
public static class ShelfReadErrors
{
    public static readonly DomainError ReaderOff = new(
        ErrorKind.Conflict, "scan.reader_off", "This server has no shelf reader set up, so it cannot read shelf pictures.");

    public static readonly DomainError NotAShelf = new(
        ErrorKind.Conflict, "scan.not_a_shelf", "That snap is a single cover or a loose item, not a shelf, so the server does not read it.");

    public static readonly DomainError SnapNotFound = new(
        ErrorKind.NotFound, "scan.snap_not_found", "That snap doesn't exist in this session, or its readings haven't been sent yet.");

    /// <summary>Temporary: answered 503 with Retry-After. The queue for the reader is full, or the wait ran out.</summary>
    public static readonly DomainError ReaderBusy = new(
        ErrorKind.Conflict, "scan.reader_busy", "The shelf reader is busy with other pictures. Try again in a moment.");

    /// <summary>Temporary: answered 503 with Retry-After. The reader did not answer in time, or could not be reached; nothing was changed.</summary>
    public static readonly DomainError ReaderUnavailable = new(
        ErrorKind.Conflict, "scan.reader_unavailable", "The shelf reader could not read that picture. Try again in a moment.");

    /// <summary>True for the two errors answered as 503 (the caller may retry).</summary>
    public static bool IsTemporary(DomainError error) => error.Code is "scan.reader_busy" or "scan.reader_unavailable";
}
