namespace CollectionMaxxing.Domain.Scanning;

/// <summary>What the phone's framing says a shot is (design §3): the phone classifies every shot from its framing and the server stores it, so engine choice, guidance and evals can condition on it.</summary>
public enum ShotKind
{
    /// <summary>A stable barcode is in view. Always wins over every other signal.</summary>
    Barcode,

    /// <summary>One dominant cover-like rectangle, or the person tapped a cover.</summary>
    SingleCover,

    /// <summary>Several text rows or spine-like rectangles, or the auto-snap gate fired.</summary>
    ShelfSegment,

    /// <summary>Single-cover geometry for a game or movie with no barcode after several frames.</summary>
    LooseItem,

    /// <summary>None of the above. The server treats it as a shelf segment.</summary>
    Unknown,
}
