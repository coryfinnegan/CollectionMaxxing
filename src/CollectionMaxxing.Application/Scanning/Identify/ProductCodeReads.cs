using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// Identifies a read by the product code printed on the cartridge or disc (ADR-031). A label's stylised art defeats both the text
/// reader and the on-device model, but the code is plain print, and the bundled table says which game it belongs to. When the code
/// names exactly one game on the platform the read names (or any, when it names none), the read becomes that game: its title and
/// platform replace the read's, its kind is a game, and the search and the scorer go on as for any read. Everything else the read
/// claimed (year, studio, completeness) stays a claim to check. When the code names no game, several games or is not a code, the
/// read is returned untouched. Pure.
/// </summary>
public static class ProductCodeReads
{
    /// <param name="facts">The read as the phone sent it.</param>
    /// <param name="productCode">The code the phone found in the picture's text; may be anything, or null.</param>
    /// <param name="serials">The table of codes.</param>
    public static ReadFacts Resolve(ReadFacts facts, string? productCode, IGameSerialLookup serials)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(serials);

        if (ProductCode.Normalize(productCode) is not { } code)
        {
            return facts;
        }

        var games = serials.Find(code, facts.Platform).DistinctBy(m => (m.Title, m.Platform)).ToList();
        if (games.Count != 1)
        {
            return facts;
        }

        var game = games[0];
        return facts with
        {
            Title = game.Title,
            Platform = game.Platform,
            Kind = WorkKind.Game,

            // A film or book format cannot be a game's; one a game comes in (cartridge, disc) stays.
            Format = facts.Format is { } format && ReleaseFormats.Fits(format, WorkKind.Game) ? format : null,
            ProductCode = code,
            ReadAs = facts.SearchText,
        };
    }
}
