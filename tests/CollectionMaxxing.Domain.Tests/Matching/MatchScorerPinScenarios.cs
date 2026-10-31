using System.Globalization;
using System.Text;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Matching;

internal sealed record PinScenario(string Name, ScoringProfileKind Profile, ReadItem Read, IReadOnlyList<CandidateRelease> Candidates);

/// <summary>
/// Reads and candidate lists drawn from the matching tests, and the text each one scores to: the outcome, then every
/// candidate best first with its score (exact), the terms it was made of, and what the release scores on its own.
/// </summary>
internal static class MatchScorerPinScenarios
{
    private static readonly ScoringOptions Options = new();

    public static IEnumerable<string> Names => All.Select(scenario => scenario.Name);

    public static PinScenario Named(string name) => All.Single(scenario => scenario.Name == name);

    public static string Describe(PinScenario scenario)
    {
        var profile = Options.Profile(scenario.Profile);
        var result = MatchScorer.Evaluate(scenario.Read, scenario.Candidates, profile);

        var text = new StringBuilder(result.Outcome.ToString());
        foreach (var scored in result.Ranked)
        {
            text.Append('\n')
                .Append(scored.Release.Key)
                .Append(" score=").Append(Exact(scored.Score))
                .Append(" alone=").Append(Exact(MatchScorer.Score(scenario.Read, scored.Release, profile)))
                .Append(" signals=").Append(scored.ComparedSignals)
                .Append(" exactId=").Append(scored.IsExactIdentifierMatch)
                .Append(" conflict=").Append(scored.HasKnownConflict)
                .Append(" terms=[")
                .AppendJoin(", ", scored.Terms.Select(term => $"{term.Name}({Exact(term.Weight)})={Exact(term.Value)}{(term.Compared ? string.Empty : "?")}"))
                .Append(']');
        }

        return text.ToString();
    }

    private static string Exact(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static IReadOnlyList<PinScenario> All { get; } =
    [
        new("alien 1979 dvd, the title carries the year", ScoringProfileKind.BarcodeProduct,
            new ReadItem { Title = "Alien 1979", Format = ReleaseFormat.Dvd },
            [Film("aliens", "Aliens", ReleaseFormat.Dvd, 1986), Film("alien", "Alien", ReleaseFormat.Dvd, 1979)]),

        new("alien dvd, the year is read as a year", ScoringProfileKind.BarcodeProduct,
            new ReadItem { Title = "Alien", Year = 1979, Format = ReleaseFormat.Dvd },
            [Film("aliens", "Aliens", ReleaseFormat.Dvd, 1986), Film("alien", "Alien", ReleaseFormat.Dvd, 1979), Film("alien-nodate", "Alien", ReleaseFormat.Dvd, null)]),

        new("madden 2005 against its other years and consoles", ScoringProfileKind.LooseCartridge,
            new ReadItem { Title = "Madden NFL 2005", Platform = "PS2", Kind = WorkKind.Game },
            [
                Game("m2004", "Madden NFL 2004", "PlayStation 2", 2003), Game("m2005", "Madden NFL 2005", "PlayStation 2", 2004),
                Game("m2005x", "Madden NFL 2005", "Xbox", 2004), Game("m2003", "Madden NFL 2003", "PS2", 2002), Game("mnfl", "Madden NFL", "PS2", null),
            ]),

        new("metroid prime 2 against its subtitle and its predecessor", ScoringProfileKind.Cover,
            new ReadItem { Title = "Metroid Prime 2", Platform = "Nintendo GameCube" },
            [
                Game("echoes", "Metroid Prime 2: Echoes", "GameCube", 2004), Game("prime", "Metroid Prime", "GameCube", 2002),
                Game("prime-long", "Metroid Prime (Nintendo GameCube, 2002)", "GameCube", 2002), Game("prime2", "Metroid Prime 2", "GameCube", null),
            ]),

        new("a title read without its subtitle", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "Kitchen Confidential", Kind = WorkKind.Book },
            [
                Book("kc", "Kitchen Confidential: Adventures in the Culinary Underbelly", "Anthony Bourdain"),
                Book("kc-plain", "Kitchen Confidential", "Anthony Bourdain"),
                Book("kc-other", "Kitchen Nightmares", "Gordon Ramsay"),
            ]),

        new("the same words in another order", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "Kitchen Orchard", Kind = WorkKind.Book },
            [
                Book("the-orchard-kitchen", "The Orchard Kitchen"), Book("orchard-kitchen-recipes", "Orchard Kitchen: Recipes for a Small Plot"),
                Book("the-kitchen-orchard", "The Kitchen Orchard"), Book("orchard", "Orchard"),
            ]),

        new("a spine reads title and author as one line", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "DUNE FRANK HERBERT", Creator = "Someone Else", Kind = WorkKind.Book },
            [
                Book("dune", "Dune", "Frank Herbert"), Book("messiah", "Dune Messiah", "Frank Herbert"),
                Book("chronicles", "Dune (Dune Chronicles, Book 1)", "Frank Herbert"), Book("other", "Dune", "Someone Else"),
            ]),

        new("a junk author read hides nothing", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "Harbor Light Keepers MARA ODELL", Creator = "FRE", Kind = WorkKind.Book },
            [
                Book("keepers", "Harbor Light Keepers", "Mara Odell"), Book("keepers-two", "Harbor Light Keepers", "Mara Odell", "Jon Vale"),
                Book("harbor", "Harbor Light", "Mara Odell"), Book("harbor-two", "Harbor Light 2", "Jon Vale"),
            ]),

        new("a surname first on the spine", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "ODELL MARA harbor light keepers", Kind = WorkKind.Book },
            [Book("keepers", "Harbor Light Keepers", "Mara Odell"), Book("keepers-comma", "Harbor Light Keepers", "Odell, Mara"), Book("atlas", "Quiet Harbor Atlas", "Mara Odell")]),

        new("a book titled after its author", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "Frank Herbert", Kind = WorkKind.Book },
            [Book("self", "Frank Herbert", "Frank Herbert"), Book("dune", "Dune", "Frank Herbert")]),

        new("an author with initials and a suffix", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "J. R. R. Odell Lantern Quest", Creator = "J. R. R. Odell", Kind = WorkKind.Book },
            [Book("quest", "Lantern Quest", "J. R. R. Odell"), Book("quest-jr", "Lantern Quest", "Martin Luther Odell Jr."), Book("keep", "Lantern Keep", "J. R. R. Odell")]),

        new("a film read against a show and a game", ScoringProfileKind.BarcodeProduct,
            new ReadItem { Title = "The Matrix", Kind = WorkKind.Film },
            [
                Film("matrix", "The Matrix", null, 1999), Tv("matrix-tv", "The Matrix", 1999), Game("matrix-game", "The Matrix", "PS2", 1999),
                Film("matrix-comma", "Matrix, The", null, 1999), Film("reloaded", "The Matrix Reloaded", null, 2003),
            ]),

        new("roman numerals and digits", ScoringProfileKind.Cover,
            new ReadItem { Title = "Rocky IV", Kind = WorkKind.Film },
            [Film("rocky4", "Rocky 4", null, 1985), Film("rocky3", "Rocky III", null, 1982), Film("rocky", "Rocky", null, 1976), Film("rocky20", "Rocky XX", null, null)]),

        new("final fantasy numerals on a platform", ScoringProfileKind.LooseCartridge,
            new ReadItem { Title = "Final Fantasy VII", Platform = "PlayStation", Format = ReleaseFormat.GameDisc },
            [
                Game("ff7", "Final Fantasy VII", "PlayStation", 1997, ReleaseFormat.GameDisc), Game("ff8", "Final Fantasy VIII", "PlayStation", 1999, ReleaseFormat.GameDisc),
                Game("ff7-pc", "Final Fantasy 7", "PC", 1998, ReleaseFormat.GameDisc), Game("ff7-nodate", "Final Fantasy 7", "PSX", null),
            ]),

        new("a title with too few letters", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "2000s", Kind = WorkKind.Book },
            [Book("decade", "2000s"), Book("walt", "2000 Walt Disney World"), Book("zero", "2000")]),

        new("a short title in caps", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "UP", Kind = WorkKind.Book },
            [Book("up", "Up"), Book("up-subtitle", "Up: A Memoir"), Book("upstairs", "Upstairs")]),

        new("artwork distances for some of the candidates", ScoringProfileKind.Cover,
            new ReadItem
            {
                Title = "Super Mario 64",
                Platform = "N64",
                ArtDistances = new Dictionary<string, double> { ["m64"] = 0.2, ["m64ds"] = 0.9, ["sunshine"] = 1.6 },
            },
            [
                Game("m64", "Super Mario 64", "Nintendo 64", 1996), Game("m64ds", "Super Mario 64 DS", "Nintendo DS", 2004),
                Game("sunshine", "Super Mario Sunshine", "GameCube", 2002), Game("coverless", "Super Mario 64", "Nintendo 64", null),
            ]),

        new("artwork distances that are unusable", ScoringProfileKind.Cover,
            new ReadItem
            {
                Title = "Zelda: Ocarina of Time",
                ArtDistances = new Dictionary<string, double> { ["a"] = double.NaN, ["b"] = -1.0, ["c"] = 0.4 },
            },
            [
                Game("a", "The Legend of Zelda: Ocarina of Time", "Nintendo 64", 1998), Game("b", "The Legend of Zelda: Majora's Mask", "Nintendo 64", 2000),
                Game("c", "Zelda II: The Adventure of Link", "NES", 1987),
            ]),

        new("a loose cartridge with the console on the label", ScoringProfileKind.LooseCartridge,
            new ReadItem { Title = "SUPER MARIO 64 CARTRIDGE ONLY", Platform = "N64", Kind = WorkKind.Game },
            [
                Game("m64", "Super Mario 64", "Nintendo 64", 1996), Game("m64ds", "Super Mario 64 DS", "Nintendo DS", 2004),
                Game("kart", "Mario Kart 64", "Nintendo 64", 1996), Game("unknown-platform", "Super Mario 64", "Boxed", 1996),
            ]),

        new("an edition mark", ScoringProfileKind.Cover,
            new ReadItem { Title = "Super Mario 64", Edition = "Player's Choice", Platform = "N64" },
            [
                Game("pc", "Super Mario 64", "Nintendo 64", 1996, edition: "Players Choice"), Game("gh", "Super Mario 64", "Nintendo 64", 1996, edition: "Greatest Hits"),
                Game("plain", "Super Mario 64", "Nintendo 64", 1996), Game("pc-sub", "Super Mario 64", "Nintendo 64", 1996, edition: "Player's Choice (Collector's Edition)"),
            ]),

        new("a bare label on a tape", ScoringProfileKind.BareLabel,
            new ReadItem { Title = "SLEEP AWAY CAMP 3 TEENAGE WASTELAND", Format = ReleaseFormat.Vhs },
            [
                Film("sac3", "Sleepaway Camp III: Teenage Wasteland", ReleaseFormat.Vhs, 1989), Film("sac3-short", "Sleepaway Camp III", ReleaseFormat.Vhs, 1989),
                Film("sac", "Sleepaway Camp", ReleaseFormat.Vhs, 1983), Film("sac3-dvd", "Sleepaway Camp 3: Teenage Wasteland", ReleaseFormat.Dvd, 1989),
            ]),

        new("accents on either side", ScoringProfileKind.Cover,
            new ReadItem { Title = "Pokémon Red Version", Platform = "Game Boy" },
            [Game("red", "Pokemon Red Version", "Game Boy", 1998), Game("blue", "Pokemon Blue Version", "Game Boy", 1998), Game("red-accent", "Pokémon Red Version", "Game Boy", 1998)]),

        new("an identifier is a match whatever the title", ScoringProfileKind.BarcodeProduct,
            new ReadItem { Title = "Completely Different Words", Platform = "PS5", Identifiers = [new Identifier(IdentifierType.Gtin13, "4006381333931")] },
            [
                new CandidateRelease { Key = "by-id", Title = "The Matrix", Kind = WorkKind.Film, Identifiers = [new Identifier(IdentifierType.Gtin13, "4006381333931")] },
                Film("matrix", "The Matrix", null, 1999),
            ]),

        new("no candidates", ScoringProfileKind.Cover, new ReadItem { Title = "Anything" }, []),

        new("a read with only whitespace for a title", ScoringProfileKind.Cover,
            new ReadItem { Title = "   ", Kind = WorkKind.Film },
            [Film("matrix", "The Matrix", null, 1999), Film("alien", "Alien", null, 1979)]),

        new("a release with no title", ScoringProfileKind.Cover,
            new ReadItem { Title = "The Matrix", Kind = WorkKind.Film },
            [
                new CandidateRelease { Key = "blank", Title = "  ", Kind = WorkKind.Film },
                new CandidateRelease { Key = "punctuation", Title = "!!!", Kind = WorkKind.Film },
                Film("matrix", "The Matrix", null, 1999),
            ]),

        new("several authors and a read author who is one of them", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "Lantern Road Cookbook", Creator = "Mara Odell and Jon Vale", Kind = WorkKind.Book },
            [
                Book("two", "Lantern Road Cookbook", "Mara Odell", "Jon Vale"), Book("one", "Lantern Road Cookbook", "Mara Odell"),
                Book("ampersand", "Lantern Road Cookbook", "Mara Ann Odell & Jon Lee Vale"), Book("other", "Lantern Road Cookbook", "Tom Vale and Jerry B. Odell"),
            ]),

        new("a repeated word", ScoringProfileKind.Cover,
            new ReadItem { Title = "Camp Camp Camp" },
            [Film("camp", "Camp Camp", null, null), Film("camp3", "Camp Camp Camp", null, null), Film("camp-iii", "Camp III", null, null), Film("camp-3", "Camp 3", null, null)]),

        new("a long title with a colon and an abbreviation", ScoringProfileKind.BarcodeProduct,
            new ReadItem { Title = "Dr. Strangelove or: How I Learned to Stop Worrying and Love the Bomb", Format = ReleaseFormat.Dvd },
            [
                Film("full", "Dr. Strangelove or: How I Learned to Stop Worrying and Love the Bomb", ReleaseFormat.Dvd, 1964),
                Film("short", "Dr. Strangelove", ReleaseFormat.Dvd, 1964), Film("other", "Dr. No", ReleaseFormat.Dvd, 1962),
            ]),

        new("a title with a subtitle in brackets and a dash", ScoringProfileKind.Cover,
            new ReadItem { Title = "Star Wars - Episode IV - A New Hope (Widescreen Edition)", Format = ReleaseFormat.Dvd },
            [
                Film("anh", "Star Wars: A New Hope", ReleaseFormat.Dvd, 1977), Film("esb", "Star Wars: The Empire Strikes Back", ReleaseFormat.Dvd, 1980),
                Film("anh-long", "STAR WARS: EPISODE IV - A NEW HOPE", ReleaseFormat.Dvd, 1977), Film("star", "Star Wars", ReleaseFormat.Dvd, 1977),
            ]),

        new("years in the title and a read year", ScoringProfileKind.BarcodeProduct,
            new ReadItem { Title = "Harbor Cup 2005", Year = 2004, Kind = WorkKind.Game },
            [
                Game("hc2004", "Harbor Cup 2004", "PC", 2003), Game("hc2005", "Harbor Cup 2005", "PC", 2004), Game("hc", "Harbor Cup", "PC", 2004),
                Game("hc2005-1979", "Harbor Cup 2005 1979", "PC", 2004),
            ]),

        new("year proximity", ScoringProfileKind.BarcodeProduct,
            new ReadItem { Title = "Jaws", Year = 1975, Format = ReleaseFormat.Dvd },
            [Film("j75", "Jaws", ReleaseFormat.Dvd, 1975), Film("j76", "Jaws", ReleaseFormat.Dvd, 1976), Film("j79", "Jaws", ReleaseFormat.Dvd, 1979), Film("j2", "Jaws 2", ReleaseFormat.Dvd, 1978), Film("jn", "Jaws", ReleaseFormat.Dvd, null)]),

        new("one spine against thirty candidates", ScoringProfileKind.ShelfSpine,
            new ReadItem { Title = "THE LANTERN ROAD COOKBOOK MARA ODELL", Creator = "Mara Odel", Year = 2018, Kind = WorkKind.Book },
            ThirtyCandidates()),
    ];

    /// <summary>The candidate list of the "one spine against thirty candidates" scenario, for anything that wants to time it.</summary>
    public static IReadOnlyList<CandidateRelease> ThirtyCandidates()
    {
        string[][] rows =
        [
            ["Lantern Road Cookbook", "Mara Odell"],
            ["The Lantern Road Cookbook: Recipes from Harbor Towns", "Mara Odell", "Jon Vale"],
            ["Lantern Road", "Mara Odell"],
            ["Lantern Road: From Harbor Towns to Mountain Passes", "Odell, Mara"],
            ["A Lantern Road Dinner", "Jon Vale"],
            ["Lantern Road Dinners", "Jon Vale"],
            ["The Lantern Room", "Sarah Mayberry"],
            ["Lantern Keep", "J. R. R. Odell"],
            ["Lantern Keep II", "J. R. R. Odell"],
            ["Lantern Magazine Test Kitchen", "The Editors of Lantern Magazine"],
            ["Harbor Light Keepers", "Mara Odell"],
            ["Harbor Light", "Mara Odell"],
            ["Harbor Light 2", "Mara Odell"],
            ["Quiet Harbor Atlas", "Wren Halloway"],
            ["Quiet Harbor Stories", "Wren Halloway", "Marisol Varga"],
            ["Quiet Suppers For Long Winter Nights", "Marisol Varga"],
            ["The Orchard Kitchen", "Anh To"],
            ["Orchard Kitchen: Recipes for a Small Plot", "Anh To"],
            ["The Glass Orchard at Night", "Ann Lee"],
            ["Simple Everyday Comfort Meals Made Easy Tonight Again"],
            ["Your Kitchen Made Simple", "Kim Odell"],
            ["The Quickest Way to Make a Perfect Supper"],
            ["Café Orchard", "Mara de la Vega"],
            ["Mara Odell's Lantern Road", "Mara Odell"],
            ["Odell, Mara"],
            ["Lantern Road Cookbook, The", "Mara Odell"],
            ["Cookbook of the Lantern Road (2018)", "Mara Odell"],
            ["Lantern Road Cookbook 2018", "Mara Odell"],
            ["Lantern Road Cookbook - Second Edition", "Mara Odell"],
            ["Lantern Road Cookbook (Revised)", "Mara Odell"],
        ];

        return [.. rows.Select((row, index) => new CandidateRelease
        {
            Key = $"c{index:00}",
            Title = row[0],
            Kind = WorkKind.Book,
            Year = index % 3 == 0 ? 2018 : index % 5 == 0 ? 2016 : null,
            Creators = row[1..],
            Edition = index % 7 == 0 ? "Second Edition" : null,
        })];
    }

    private static CandidateRelease Film(string key, string title, ReleaseFormat? format, int? year) =>
        new() { Key = key, Title = title, Kind = WorkKind.Film, Format = format, Year = year };

    private static CandidateRelease Tv(string key, string title, int? year) =>
        new() { Key = key, Title = title, Kind = WorkKind.Tv, Year = year };

    private static CandidateRelease Game(string key, string title, string? platform, int? year, ReleaseFormat? format = null, string? edition = null) =>
        new() { Key = key, Title = title, Kind = WorkKind.Game, Platform = platform, Year = year, Format = format, Edition = edition };

    private static CandidateRelease Book(string key, string title, params string[] creators) =>
        new() { Key = key, Title = title, Kind = WorkKind.Book, Creators = creators };
}
