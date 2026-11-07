using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A line of the shot that is a console's name tells the snap's reads that name no platform which console the shot prints.
public class IdentifyCleaningPrintedPlatformTests
{
    private static IdentifyItemInput Read(string title, string? platform = null, params string[] lines) =>
        new(null, null, title, title, null, null, null, null, null, null, platform, null, Lines: lines.Length > 0 ? lines : null);

    [Fact]
    public void A_line_that_is_a_consoles_name_tells_the_snaps_reads_that_name_no_platform()
    {
        var cleaned = IdentifyCleaning.Clean([Read("ICO", lines: "ICO"), Read("TROPIC THUNDER", lines: ["TROPIC THUNDER", "PlayStation.2"])]);

        cleaned.ShouldAllBe(c => c.Facts.PlatformContext == "PS2");
    }

    [Fact]
    public void A_cover_reads_lines_count_too()
    {
        var cleaned = IdentifyCleaning.Clean([Read("ICO", lines: ["ICO", "PlayStation 2"])], IdentifyKind.Cover);

        cleaned.Single().Facts.PlatformContext.ShouldBe("PS2");
    }

    [Fact]
    public void A_line_that_holds_a_consoles_name_among_other_words_prints_no_platform()
    {
        var cleaned = IdentifyCleaning.Clean([Read("ICO"), Read("SECRETS", lines: "PlayStation 2 Secret Codes")]);

        cleaned.ShouldAllBe(c => c.Facts.PlatformContext == null);
    }

    [Fact]
    public void A_reads_own_platform_guess_prints_nothing()
    {
        var cleaned = IdentifyCleaning.Clean([Read("ICO"), Read("TROPIC THUNDER", platform: "Nintendo Switch")]);

        cleaned.ShouldAllBe(c => c.Facts.PlatformContext == null);
    }

    [Fact]
    public void A_read_that_names_its_platform_is_told_nothing()
    {
        var cleaned = IdentifyCleaning.Clean([Read("ICO", platform: "PS3"), Read("TROPIC THUNDER", lines: "PlayStation.2")]);

        cleaned[0].Facts.PlatformContext.ShouldBeNull();
        cleaned[1].Facts.PlatformContext.ShouldBe("PS2");
    }

    [Fact]
    public void The_other_engines_printed_platforms_count_as_the_snaps_and_are_one_sorted_value()
    {
        var cleaned = IdentifyCleaning.Clean([Read("ICO", lines: "SNES")], snapPlatforms: ["PS2"]);

        cleaned.Single().Facts.PlatformContext.ShouldBe("PS2|SNES");
    }

    [Fact]
    public void Printed_platforms_are_the_canonical_names_of_the_whole_lines_that_are_one()
    {
        IdentifyCleaning.PrintedPlatforms([Read("A", lines: ["PlayStation.2", "ICO"]), Read("B", lines: ["Nintendo Switch", "PlayStation 2"])])
            .ShouldBe(["PS2", "Switch"]);
    }
}
