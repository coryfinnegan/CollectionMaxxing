namespace CollectionMaxxing.Application.GameSerials;

/// <summary>
/// A game a product code names: the table's spelling of the code that matched, the game's title (<c>Clay Fighter</c>,
/// <c>Donkey Kong Country 2: Diddy's Kong Quest</c>) and its platform as <c>PlatformNames</c> spells it (<c>SNES</c>, <c>PS1</c>).
/// </summary>
public sealed record GameSerialMatch(string Code, string Title, string Platform);

/// <summary>
/// Turns the product code printed on a cartridge label or a disc into the game it belongs to (ADR-031). The table behind it is
/// bundled with the server and read once; a lookup touches no network and no database.
/// </summary>
public interface IGameSerialLookup
{
    /// <summary>
    /// The games a code names, in title order. The code is tried as read, then without a trailing region suffix
    /// (<c>SNS-8C-USA</c> finds <c>SNS-8C</c>), then with each suffix (<c>SNS-ADNE</c> finds <c>SNS-ADNE-USA</c>); the first step
    /// that finds anything answers. A code resolves only within its own platform: when <paramref name="platform"/> names a
    /// platform the matcher knows, only that platform's games come back, and none when the code belongs to another. A platform
    /// word that names no console does not count as a claim. Text that is not shaped like a code finds nothing.
    /// </summary>
    IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null);
}
