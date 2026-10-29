using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// Drops what the on-device model put in a field that cannot hold it, before the read is scored or stored. Real reads
/// carried the model's own enum words as the platform ("boxed", "looseCart", "book"), a loose-cart completeness on a book
/// cover (which scored the book as a cartridge), ratings, bestseller lines and ISBNs as the edition, and a series line,
/// brand or placeholder as the author. A field that is dropped is simply not a claim; nothing is invented in its place. Pure.
/// </summary>
public static class ReadSanitizer
{
    /// <summary>Words the model writes as a platform that name no console (its kind and completeness words, formats).</summary>
    private static readonly HashSet<string> NotAPlatform = new(StringComparer.Ordinal)
    {
        "book", "books", "novel", "boxed", "box", "complete", "loosecart", "loose cart", "cart only", "cartonly", "cartridge",
        "cart", "disconly", "disc only", "disc", "manualvisible", "manual visible", "manual", "game", "games", "video game",
        "videogame", "other", "none", "unknown", "n a", "na", "null", "nil", "paperback", "hardcover", "dvd", "vhs", "bluray",
        "blu ray", "uhd", "uhdbluray", "4k", "film", "movie", "tv", "cd",
    };

    /// <summary>Age ratings (ESRB, PEGI, MPAA, TV) the model reads off a box corner as an edition.</summary>
    private static readonly HashSet<string> Ratings = new(StringComparer.Ordinal)
    {
        "e", "e10", "ec", "t", "m", "ao", "rp", "everyone", "everyone 10", "teen", "mature", "mature 17", "adults only",
        "rating pending", "3", "7", "12", "16", "18", "g", "pg", "pg 13", "r", "nc 17", "nr", "not rated", "tv g", "tv pg",
        "tv 14", "tv ma", "tv y", "tv y7",
    };

    private static readonly string[] RatingPrefixes = ["esrb", "pegi", "usk", "rated "];

    /// <summary>Cover marketing the model reads as an edition: none of it tells two releases apart.</summary>
    private static readonly string[] Taglines =
    [
        "bestsell", "best sell", "new york times", "usa today", "author of", "award", "winner", "major motion picture",
        "soon to be a", "isbn", "recipes", "from the author", "by the author", "copies sold", "million copies",
    ];

    private const int IsbnDigits = 9;

    /// <summary>What the model writes as the author when it read none.</summary>
    private static readonly HashSet<string> NotACreator = new(StringComparer.Ordinal)
    {
        "author", "authors", "the author", "the authors", "unknown", "unknown author", "various", "various authors", "n a", "na",
        "none", "null",
    };

    public static ReadFacts Sanitize(ReadFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // "Other" is the reader saying it does not know: no kind, so every provider is asked.
        var kind = facts.Kind == WorkKind.Other ? null : facts.Kind;
        var format = facts.Format;
        var platform = facts.Platform;

        if (!string.IsNullOrWhiteSpace(platform))
        {
            if (kind is WorkKind.Film or WorkKind.Tv && PlatformNames.IsKnown(platform))
            {
                // Only games name a console: a PS2 game sits in a DVD case and a PS3 one on a Blu-ray.
                kind = WorkKind.Game;
                format = ReleaseFormats.IsVideo(format) ? ReleaseFormat.GameDisc : format;
            }
            else if (kind is WorkKind.Book or WorkKind.Film or WorkKind.Tv || NotAPlatform.Contains(PlatformNames.Normalize(platform)))
            {
                platform = null;
            }
        }

        return facts with
        {
            Kind = kind,
            Format = format,
            Platform = string.IsNullOrWhiteSpace(platform) ? null : platform,
            Completeness = CompletenessFor(kind, format, facts.Completeness),
            Edition = IsEditionMark(facts.Edition) ? facts.Edition : null,
            Creator = IsCreator(facts.Creator, facts.SearchText) ? facts.Creator : null,
        };
    }

    // A book has no cartridge or disc to lose; a disc can't be a loose cartridge (a bare VHS tape can).
    private static ItemCompleteness? CompletenessFor(WorkKind? kind, ReleaseFormat? format, ItemCompleteness? completeness) => kind switch
    {
        WorkKind.Book => null,
        WorkKind.Film or WorkKind.Tv when completeness == ItemCompleteness.CartOnly
            && ReleaseFormats.IsKnown(format) && format is not (ReleaseFormat.Vhs or ReleaseFormat.Betamax) => null,
        _ => completeness,
    };

    // An author is a claim of its own only when it says something the title does not: the model also copies a series line
    // or brand off the spine (a series' name for a book of that series) as the author. A real author the title carries (a
    // spine's "DUNE FRANK HERBERT") is still found: the scorer looks for each release's own author in the title.
    private static bool IsCreator(string? creator, string? title)
    {
        if (string.IsNullOrWhiteSpace(creator) || NotACreator.Contains(PlatformNames.Normalize(creator)))
        {
            return false;
        }

        var titleWords = TitleNormalizer.Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var creatorWords = TitleNormalizer.Normalize(creator).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return creatorWords.Length > 0 && !creatorWords.All(titleWords.Contains);
    }

    private static bool IsEditionMark(string? edition)
    {
        var text = PlatformNames.Normalize(edition);
        if (text.Length < 2)
        {
            return false;
        }

        if (text == "a novel" || Ratings.Contains(text) || RatingPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)))
        {
            return false;
        }

        return edition!.Count(char.IsAsciiDigit) < IsbnDigits && !Taglines.Any(t => text.Contains(t, StringComparison.Ordinal));
    }
}
