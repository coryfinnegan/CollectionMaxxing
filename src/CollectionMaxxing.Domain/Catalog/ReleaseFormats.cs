namespace CollectionMaxxing.Domain.Catalog;

/// <summary>What a release format says about the kind of work it carries.</summary>
public static class ReleaseFormats
{
    /// <summary>
    /// Whether a work of this kind comes in this format: a DVD or a VHS tape holds a film or a show, a cartridge or a game
    /// disc a game, a paperback a book. <see cref="ReleaseFormat.Other"/> and <see cref="ReleaseFormat.Unknown"/> fit nothing.
    /// </summary>
    public static bool Fits(ReleaseFormat format, WorkKind kind) => format switch
    {
        ReleaseFormat.Hardcover or ReleaseFormat.Paperback or ReleaseFormat.MassMarket or ReleaseFormat.BoardBook
            or ReleaseFormat.SpiralBound => kind == WorkKind.Book,
        ReleaseFormat.Vhs or ReleaseFormat.Betamax or ReleaseFormat.Laserdisc or ReleaseFormat.Dvd
            or ReleaseFormat.Bluray or ReleaseFormat.UhdBluray => kind is WorkKind.Film or WorkKind.Tv,
        ReleaseFormat.GameCartridge or ReleaseFormat.GameDisc or ReleaseFormat.GameCard => kind == WorkKind.Game,
        _ => false,
    };

    /// <summary>A format the catalogue actually knows: neither missing nor <see cref="ReleaseFormat.Unknown"/>.</summary>
    public static bool IsKnown(ReleaseFormat? format) => format is { } known && known != ReleaseFormat.Unknown;

    /// <summary>The video formats: a read in one of these is a film or a show.</summary>
    public static bool IsVideo(ReleaseFormat? format) => format is { } known && Fits(known, WorkKind.Film);
}
