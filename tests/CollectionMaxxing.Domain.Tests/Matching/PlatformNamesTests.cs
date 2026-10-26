using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Locked acceptance tests for CAT-1. Do not weaken, skip or delete.
public class PlatformNamesTests
{
    [Theory]
    [InlineData("N64", "N64")]
    [InlineData("n64", "N64")]
    [InlineData("Nintendo 64", "N64")]
    [InlineData("nintendo-64", "N64")]
    [InlineData("Nintendo GameCube", "GameCube")]
    [InlineData("Game Cube", "GameCube")]
    [InlineData("GameCube", "GameCube")]
    [InlineData("PS2", "PS2")]
    [InlineData("PlayStation 2", "PS2")]
    [InlineData("Sony PlayStation 2", "PS2")]
    [InlineData("PlayStation", "PS1")]
    [InlineData("PSX", "PS1")]
    [InlineData("Super Nintendo Entertainment System", "SNES")]
    [InlineData("Super Nintendo", "SNES")]
    [InlineData("NES", "NES")]
    [InlineData("Nintendo Entertainment System", "NES")]
    [InlineData("Xbox 360", "Xbox 360")]
    [InlineData("Microsoft Xbox 360", "Xbox 360")]
    [InlineData("Xbox", "Xbox")]
    [InlineData("Game Boy", "Game Boy")]
    [InlineData("Gameboy", "Game Boy")]
    [InlineData("Game Boy Advance", "Game Boy Advance")]
    [InlineData("GBA", "Game Boy Advance")]
    [InlineData("Nintendo Switch", "Switch")]
    [InlineData("Switch 2", "Switch 2")]
    [InlineData("Nintendo Switch 2", "Switch 2")]
    [InlineData("Sega Genesis", "Genesis")]
    [InlineData("Mega Drive", "Genesis")]
    [InlineData("Sega Dreamcast", "Dreamcast")]
    public void Common_names_for_a_platform_all_canonicalise_to_one_name(string name, string canonical)
    {
        PlatformNames.Canonicalize(name).ShouldBe(canonical);
    }

    [Fact]
    public void An_unknown_platform_canonicalises_to_its_tidied_self_so_two_equal_spellings_still_agree()
    {
        PlatformNames.Canonicalize("Atari 2600").ShouldBe(PlatformNames.Canonicalize("atari  2600"));
        PlatformNames.Canonicalize("Atari 2600").ShouldNotBe(PlatformNames.Canonicalize("Atari 7800"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_canonicalises_to_nothing(string? name)
    {
        PlatformNames.Canonicalize(name).ShouldBeNull();
    }

    [Fact]
    public void Different_consoles_stay_different()
    {
        PlatformNames.Canonicalize("N64").ShouldNotBe(PlatformNames.Canonicalize("GameCube"));
        PlatformNames.Canonicalize("PS2").ShouldNotBe(PlatformNames.Canonicalize("PS3"));
        PlatformNames.Canonicalize("Game Boy").ShouldNotBe(PlatformNames.Canonicalize("Game Boy Color"));
        PlatformNames.Canonicalize("Xbox").ShouldNotBe(PlatformNames.Canonicalize("Xbox 360"));
        PlatformNames.Canonicalize("Switch").ShouldNotBe(PlatformNames.Canonicalize("Switch 2"));
    }
}
