using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// What a shot was once it has been read. The phone frames every auto snap as a shelf segment (the gate firing reads as
/// one) and most cover shots too, because a cover has several rows of text; but a shelf read that found exactly one item
/// was a shot of one item. Its candidate is a cover shot, so a close call gets the artwork comparison and the server's
/// cover verification instead of the text-only treatment a shelf of spines gets (#519). Every other shot keeps the
/// framing's label (an unclassified shot already compares artwork), and the snap always does.
/// </summary>
public static class ShotReading
{
    public static ShotKind Of(ShotKind? framed, int itemsRead) => framed switch
    {
        ShotKind.ShelfSegment when itemsRead == 1 => ShotKind.SingleCover,
        null => ShotKind.Unknown,
        _ => framed.Value,
    };
}
