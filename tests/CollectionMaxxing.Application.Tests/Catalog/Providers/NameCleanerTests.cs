using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Tests.Catalog.Providers;

// Locked acceptance tests for CAT-1 (docs/02 section 3: the deterministic NameCleaner). Do not weaken, skip or delete.
// The names are the kind of thing a UPC database returns for DVD, VHS, Blu-ray, cartridge and loose-disc barcodes.
public class NameCleanerTests
{
    private static readonly (string Name, string Title, string? Platform, ReleaseFormat? Format, int? Year)[] Cases =
    [
        // ---- video ----
        ("The Matrix [DVD]", "The Matrix", null, ReleaseFormat.Dvd, null),
        ("Alien (Widescreen) [DVD]", "Alien", null, ReleaseFormat.Dvd, null),
        ("Jaws (Blu-ray Disc, 2012)", "Jaws", null, ReleaseFormat.Bluray, 2012),
        ("The Silence of the Lambs (VHS, 1991)", "The Silence of the Lambs", null, ReleaseFormat.Vhs, 1991),
        ("Ghostbusters VHS", "Ghostbusters", null, ReleaseFormat.Vhs, null),
        ("Back to the Future Trilogy [Blu-ray]", "Back to the Future Trilogy", null, ReleaseFormat.Bluray, null),
        ("Ghostbusters II 4K Ultra HD Blu-ray", "Ghostbusters II", null, ReleaseFormat.UhdBluray, null),
        ("STAR WARS: EPISODE IV - A NEW HOPE (WIDESCREEN EDITION) DVD", "STAR WARS: EPISODE IV - A NEW HOPE", null, ReleaseFormat.Dvd, null),
        ("  The   Matrix   [DVD]  ", "The Matrix", null, ReleaseFormat.Dvd, null),

        // ---- games: cartridges and discs ----
        ("Metroid Prime (Nintendo GameCube, 2002)", "Metroid Prime", "GameCube", null, 2002),
        ("Super Mario 64 - Nintendo 64 Cartridge Only", "Super Mario 64", "N64", ReleaseFormat.GameCartridge, null),
        ("Brand New Factory Sealed The Legend of Zelda: Ocarina of Time N64", "The Legend of Zelda: Ocarina of Time", "N64", null, null),
        ("Halo 3 Xbox 360 Game Disc", "Halo 3", "Xbox 360", ReleaseFormat.GameDisc, null),
        ("Pokemon Red Version Game Boy Cartridge", "Pokemon Red Version", "Game Boy", ReleaseFormat.GameCartridge, null),
        ("Resident Evil 4 for PlayStation 2", "Resident Evil 4", "PS2", null, null),
        ("Zelda II: The Adventure of Link NES Cartridge", "Zelda II: The Adventure of Link", "NES", ReleaseFormat.GameCartridge, null),
        ("Madden NFL 2005 PS2", "Madden NFL 2005", "PS2", null, null),
        ("Mortal Kombat (Sega Genesis) - Factory Sealed", "Mortal Kombat", "Genesis", null, null),
        ("Final Fantasy VII PlayStation Game Disc", "Final Fantasy VII", "PS1", ReleaseFormat.GameDisc, null),

        // ---- books ----
        ("Jurassic Park (Paperback)", "Jurassic Park", null, ReleaseFormat.Paperback, null),
        ("Dune [Mass Market Paperback]", "Dune", null, ReleaseFormat.MassMarket, null),

        // ---- names that must be left alone ----
        ("Mario Kart Wii", "Mario Kart Wii", null, null, null),
        ("Wii Sports", "Wii Sports", null, null, null),
        ("PlayStation All-Stars Battle Royale", "PlayStation All-Stars Battle Royale", null, null, null),
        ("1984", "1984", null, null, null),
        ("Blade Runner: The Final Cut", "Blade Runner: The Final Cut", null, null, null),
        ("New Super Mario Bros.", "New Super Mario Bros.", null, null, null),
        ("Dr. Strangelove or: How I Learned to Stop Worrying and Love the Bomb", "Dr. Strangelove or: How I Learned to Stop Worrying and Love the Bomb", null, null, null),
        ("Used Cars", "Used Cars", null, null, null),
        ("Hellboy (Director's Cut)", "Hellboy (Director's Cut)", null, null, null),
        ("Halo: Combat Evolved", "Halo: Combat Evolved", null, null, null),
    ];

    public static TheoryData<string, string, string?, ReleaseFormat?, int?> RealWorldNames
    {
        get
        {
            var data = new TheoryData<string, string, string?, ReleaseFormat?, int?>();
            foreach (var (name, title, platform, format, year) in Cases)
            {
                data.Add(name, title, platform, format, year);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RealWorldNames))]
    public void Real_world_product_names_are_cleaned_into_a_title_and_whatever_can_be_pulled_out(
        string productName, string title, string? platform, ReleaseFormat? format, int? year)
    {
        var cleaned = NameCleaner.Clean(productName);

        cleaned.Title.ShouldBe(title);
        cleaned.Platform.ShouldBe(platform);
        cleaned.Format.ShouldBe(format);
        cleaned.Year.ShouldBe(year);
    }

    [Fact]
    public void There_are_at_least_fifteen_real_world_names_and_some_that_must_be_left_alone()
    {
        Cases.Length.ShouldBeGreaterThanOrEqualTo(15);
        Cases.Count(c => c.Name == c.Title).ShouldBeGreaterThanOrEqualTo(5);
    }

    [Theory]
    [InlineData("Super Mario 64 (Nintendo 64) Cartridge Only Brand New", "Super Mario 64", "N64", ReleaseFormat.GameCartridge)]
    [InlineData("super mario 64 cartridge only", "super mario 64", null, ReleaseFormat.GameCartridge)]
    [InlineData("SUPER MARIO 64 CARTRIDGE ONLY", "SUPER MARIO 64", null, ReleaseFormat.GameCartridge)]
    [InlineData("Alien (Widescreen) (Fullscreen) [DVD]", "Alien", null, ReleaseFormat.Dvd)]
    [InlineData("Alien - DVD", "Alien", null, ReleaseFormat.Dvd)]
    [InlineData("Alien, DVD", "Alien", null, ReleaseFormat.Dvd)]
    [InlineData("Alien Blu ray", "Alien", null, ReleaseFormat.Bluray)]
    [InlineData("Alien Bluray", "Alien", null, ReleaseFormat.Bluray)]
    [InlineData("Alien LaserDisc", "Alien", null, ReleaseFormat.Laserdisc)]
    public void Case_separators_and_spellings_of_the_noise_do_not_matter(
        string productName, string title, string? platform, ReleaseFormat? format)
    {
        var cleaned = NameCleaner.Clean(productName);

        cleaned.Title.ShouldBe(title);
        cleaned.Platform.ShouldBe(platform);
        cleaned.Format.ShouldBe(format);
    }

    [Theory]
    [InlineData("Super Mario Sunshine [GameCube] Brand New", "Super Mario Sunshine", "GameCube")]
    [InlineData("Wii Fit Plus for Wii", "Wii Fit Plus", "Wii")]
    [InlineData("Wii Fit Plus - Wii", "Wii Fit Plus", "Wii")]
    [InlineData("Animal Crossing (Nintendo Switch)", "Animal Crossing", "Switch")]
    public void Risky_single_word_platforms_are_only_taken_when_something_marks_them_as_a_platform(
        string productName, string title, string platform)
    {
        var cleaned = NameCleaner.Clean(productName);

        cleaned.Title.ShouldBe(title);
        cleaned.Platform.ShouldBe(platform);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[DVD]")]
    [InlineData("()")]
    [InlineData(" - ")]
    public void Empty_or_noise_only_names_give_an_empty_title_and_never_throw(string? junk)
    {
        var cleaned = NameCleaner.Clean(junk);

        cleaned.Title.ShouldBe(string.Empty);
    }

    [Fact]
    public void A_name_that_is_only_a_platform_or_format_keeps_its_words_as_the_title_rather_than_vanishing()
    {
        // "Switch" the film or "Xbox" the game: stripping everything would leave nothing to search for.
        NameCleaner.Clean("Wii").Title.ShouldNotBeEmpty();
        NameCleaner.Clean("DVD").Title.ShouldNotBeEmpty();
    }

    [Fact]
    public void Cleaning_is_deterministic_and_idempotent()
    {
        foreach (var (name, _, _, _, _) in Cases)
        {
            var once = NameCleaner.Clean(name);

            NameCleaner.Clean(name).ShouldBe(once);
            NameCleaner.Clean(once.Title).Title.ShouldBe(once.Title);
        }
    }

    [Fact]
    public void The_unknown_brackets_in_a_title_are_kept()
    {
        NameCleaner.Clean("Alien (Director's Cut) [DVD]").Title.ShouldBe("Alien (Director's Cut)");
        NameCleaner.Clean("Alien (Director's Cut) [DVD]").Format.ShouldBe(ReleaseFormat.Dvd);
    }
}
