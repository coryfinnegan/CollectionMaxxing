using System.IO.Compression;
using System.Text;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Infrastructure.GameSerials;

namespace CollectionMaxxing.Infrastructure.Tests.GameSerials;

// The bundled table is real data (libretro-database, pinned): these tests look games up in it as the scanner does, with the
// separators and region suffixes a label and the ocr give a code, and check the table itself is the shape the lookup relies on.
public class GameSerialTableTests
{
    private static readonly GameSerialTable Table = new();

    [Fact]
    public void A_code_with_a_region_suffix_finds_the_games_shorter_form_in_the_list()
    {
        var found = Table.Find("SNS-8C-USA").ShouldHaveSingleItem();

        found.Title.ShouldBe("Clay Fighter");
        found.Platform.ShouldBe("SNES");
        found.Code.ShouldBe("SNS-8C");
    }

    [Fact]
    public void A_code_spelled_as_the_list_has_it_is_found_as_read()
    {
        var found = Table.Find("SNS-ADNE-USA").ShouldHaveSingleItem();

        found.Title.ShouldBe("Donkey Kong Country 2: Diddy's Kong Quest");
        found.Platform.ShouldBe("SNES");
    }

    [Theory]
    [InlineData("sns-adne-usa")]
    [InlineData("SNS ADNE-USA")]
    [InlineData("SNS_ADNE_USA")]
    [InlineData("  SNS - ADNE - USA ")]
    public void The_separators_and_case_the_ocr_gave_a_code_do_not_matter(string read) =>
        Table.Find(read).ShouldHaveSingleItem().Title.ShouldBe("Donkey Kong Country 2: Diddy's Kong Quest");

    [Fact]
    public void A_code_printed_without_its_region_finds_the_regions_entry()
    {
        // The list has SNS-ADNE-USA; the label may print only SNS-ADNE.
        Table.Find("SNS-ADNE").ShouldHaveSingleItem().Title.ShouldBe("Donkey Kong Country 2: Diddy's Kong Quest");
    }

    [Fact]
    public void A_nintendo_64_cartridge_code_is_found()
    {
        var found = Table.Find("NUS-NSME-USA").ShouldHaveSingleItem();

        found.Title.ShouldBe("Super Mario 64");
        found.Platform.ShouldBe("N64");
    }

    [Theory]
    [InlineData("SLUS-00594")]
    [InlineData("SLUS_005.94")]
    [InlineData("SLUS 00594")]
    [InlineData("slus00594")]
    public void A_playstation_disc_code_is_found_however_the_print_spells_it(string read)
    {
        var found = Table.Find(read);

        found.ShouldNotBeEmpty();
        found.ShouldAllBe(m => m.Title == "Metal Gear Solid" && m.Platform == "PS1");
    }

    [Fact]
    public void A_playstation_2_code_is_found()
    {
        var found = Table.Find("SLUS-20001"); // a disc the redump PlayStation 2 list holds

        found.ShouldNotBeEmpty();
        found.ShouldAllBe(m => m.Platform == "PS2");
    }

    [Fact]
    public void A_code_nothing_in_the_list_has_finds_nothing()
    {
        Table.Find("ZZZ-9999-USA").ShouldBeEmpty();
        Table.Find("SNS-ZZZZ").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Wrangler's 2")]
    [InlineData("12345")]
    public void Something_that_is_not_a_code_finds_nothing(string? read) => Table.Find(read).ShouldBeEmpty();

    [Fact]
    public void A_code_resolves_only_within_its_platform()
    {
        Table.Find("SNS-8C-USA", "Nintendo 64").ShouldBeEmpty();
        Table.Find("SNS-8C-USA", "PS1").ShouldBeEmpty();
        Table.Find("SNS-8C-USA", "Super Nintendo").ShouldHaveSingleItem().Title.ShouldBe("Clay Fighter");
        Table.Find("SNS-8C-USA", "SUPER NINTENDO").ShouldHaveSingleItem().Title.ShouldBe("Clay Fighter");
        Table.Find("SNS-8C-USA", "snes").ShouldHaveSingleItem().Title.ShouldBe("Clay Fighter");
    }

    [Fact]
    public void A_platform_word_no_platform_goes_by_does_not_block_the_code()
    {
        Table.Find("SNS-8C-USA", "boxed").ShouldHaveSingleItem().Title.ShouldBe("Clay Fighter");
        Table.Find("SNS-8C-USA", "  ").ShouldHaveSingleItem().Title.ShouldBe("Clay Fighter");
    }

    [Fact]
    public void A_code_two_platforms_share_is_narrowed_by_the_platform_the_read_names()
    {
        // The Game Boy and Game Boy Color lists both hold this cartridge code, for different games.
        var both = Table.Find("CGB-BQQJ-JPN");
        both.Select(m => m.Platform).ShouldBe(["Game Boy", "Game Boy Color"], ignoreOrder: true);

        Table.Find("CGB-BQQJ-JPN", "Game Boy Color").ShouldHaveSingleItem().Platform.ShouldBe("Game Boy Color");
        Table.Find("CGB-BQQJ-JPN", "GBC").ShouldHaveSingleItem().Platform.ShouldBe("Game Boy Color");
    }

    [Fact]
    public void A_code_that_names_several_games_returns_all_of_them_in_title_order()
    {
        var found = Table.Find("ULUS-10388");

        found.Select(m => m.Title).ShouldBe(["PES 2009: Pro Evolution Soccer", "Pro Evolution Soccer 2009"]);
        found.ShouldAllBe(m => m.Platform == "PSP");
    }

    [Fact]
    public void A_code_as_read_is_answered_by_its_own_entry_before_any_suffix_is_tried()
    {
        // The list also holds SLUS-00594-0 for the same game; the code read as SLUS-00594 matches its own entry only.
        Table.Find("SLUS-00594").ShouldHaveSingleItem().Code.ShouldBe("SLUS-00594");
        Table.Find("SLUS-00594-0").ShouldHaveSingleItem().Code.ShouldBe("SLUS-00594-0");
    }

    // ---- the table itself -----------------------------------------------------------------------------------------

    private static List<(string Code, string Platform, string Title)> Rows()
    {
        using var stream = typeof(GameSerialTable).Assembly.GetManifestResourceStream(GameSerialTable.ResourceName)
            ?? throw new InvalidOperationException("The embedded table is missing.");
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);
        var rows = new List<(string, string, string)>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            fields.Length.ShouldBe(3, $"A row is code, platform and title: {line}");
            rows.Add((fields[0], fields[1], fields[2]));
        }

        return rows;
    }

    [Fact]
    public void Every_code_in_the_table_is_already_in_normal_form()
    {
        var notNormal = Rows().Where(r => ProductCode.Normalize(r.Code) != r.Code).Take(10).Select(r => r.Code).ToList();

        notNormal.ShouldBeEmpty("The script and ProductCode.Normalize must spell a code the same way.");
    }

    [Fact]
    public void Every_platform_in_the_table_is_a_platform_the_matcher_knows_by_its_own_name()
    {
        var platforms = Rows().Select(r => r.Platform).Distinct().ToList();

        platforms.Count.ShouldBeGreaterThan(20);
        foreach (var platform in platforms)
        {
            PlatformNames.TryGetCanonical(platform, out var canonical).ShouldBeTrue($"{platform} is not a platform PlatformNames knows");
            canonical.ShouldBe(platform);
        }
    }

    [Fact]
    public void The_table_is_sorted_has_no_repeated_rows_and_no_blank_titles()
    {
        var rows = Rows();
        var lines = rows.Select(r => $"{r.Code}\t{r.Platform}\t{r.Title}").ToList();

        lines.Count.ShouldBeGreaterThan(50_000);
        lines.ShouldBe(lines.Order(StringComparer.Ordinal).ToList());
        lines.Distinct().Count().ShouldBe(lines.Count);
        rows.ShouldAllBe(r => r.Title.Trim() == r.Title && r.Title.Length > 0);
    }

    [Fact]
    public void The_table_names_the_commit_it_was_made_from_and_stays_small()
    {
        using var stream = typeof(GameSerialTable).Assembly.GetManifestResourceStream(GameSerialTable.ResourceName)!;
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);

        reader.ReadLine()!.ShouldMatch("^#libretro-database [0-9a-f]{40}$");
        stream.Length.ShouldBeLessThan(3 * 1024 * 1024);
    }
}
