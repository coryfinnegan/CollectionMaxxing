using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A cartridge's label art defeats the readers, but the product code printed on it is plain text. When that code names exactly one
// game, the read is identified as that game (the code is a fact from a table, the garbled title only a guess); in every other
// case the read is left exactly as the phone sent it.
public class ProductCodeReadsTests
{
    private sealed class FakeSerials(params GameSerialMatch[] matches) : IGameSerialLookup
    {
        public List<(string? Code, string? Platform)> Asked { get; } = [];

        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null)
        {
            Asked.Add((code, platform));
            return matches;
        }
    }

    private static readonly GameSerialMatch ClayFighter = new("SNS-8C", "Clay Fighter", "SNES");

    private static ReadFacts GarbledRead(string? platform = "SUPER NINTENDO") => new(
        Title: "Wrangler's 2",
        Text: "WRANGLER'S 2 SUPER NINTENDO",
        Creator: "Interplay",
        Edition: null,
        Completeness: ItemCompleteness.CartOnly,
        Kind: null,
        Format: null,
        Year: 1993,
        Platform: platform);

    [Fact]
    public void A_code_that_names_one_game_makes_the_read_that_game()
    {
        var read = ProductCodeReads.Resolve(GarbledRead(), "SNS-8C-USA", new FakeSerials(ClayFighter));

        read.Title.ShouldBe("Clay Fighter");
        read.Platform.ShouldBe("SNES");
        read.Kind.ShouldBe(WorkKind.Game);
        read.SearchText.ShouldBe("Clay Fighter");
    }

    [Fact]
    public void The_read_keeps_what_it_was_and_says_which_code_decided_the_title()
    {
        var read = ProductCodeReads.Resolve(GarbledRead(), "sns 8c-usa", new FakeSerials(ClayFighter));

        read.ProductCode.ShouldBe("SNS-8C-USA");
        read.ReadAs.ShouldBe("Wrangler's 2");
        read.Text.ShouldBe("WRANGLER'S 2 SUPER NINTENDO");
        read.Year.ShouldBe(1993);
        read.Creator.ShouldBe("Interplay");
        read.Completeness.ShouldBe(ItemCompleteness.CartOnly);
        read.ShownText.ShouldBe("WRANGLER'S 2 SUPER NINTENDO");
    }

    [Fact]
    public void The_lookup_is_asked_with_the_platform_the_read_names()
    {
        var serials = new FakeSerials(ClayFighter);

        ProductCodeReads.Resolve(GarbledRead("Nintendo 64"), "SNS-8C-USA", serials);

        serials.Asked.ShouldBe([("SNS-8C-USA", "Nintendo 64")]);
    }

    [Fact]
    public void A_code_that_names_no_game_leaves_the_read_alone()
    {
        var read = GarbledRead();

        ProductCodeReads.Resolve(read, "SNS-ZZZZ", new FakeSerials()).ShouldBe(read);
    }

    [Fact]
    public void A_code_that_names_several_games_leaves_the_read_alone()
    {
        var read = GarbledRead();
        var serials = new FakeSerials(ClayFighter, new GameSerialMatch("SNS-8C", "Clay Fighter 2", "SNES"));

        ProductCodeReads.Resolve(read, "SNS-8C", serials).ShouldBe(read);
    }

    [Fact]
    public void The_same_game_listed_twice_is_still_one_game()
    {
        var serials = new FakeSerials(ClayFighter, new GameSerialMatch("SNS-8C-USA", "Clay Fighter", "SNES"));

        ProductCodeReads.Resolve(GarbledRead(), "SNS-8C", serials).Title.ShouldBe("Clay Fighter");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a code!")]
    [InlineData("12345")]
    public void Without_a_code_shaped_like_one_the_read_is_left_alone_and_nothing_is_looked_up(string? code)
    {
        var read = GarbledRead();
        var serials = new FakeSerials(ClayFighter);

        ProductCodeReads.Resolve(read, code, serials).ShouldBe(read);
        serials.Asked.ShouldBeEmpty();
    }

    [Fact]
    public void A_format_that_no_game_comes_in_is_dropped_but_a_game_format_stays()
    {
        var serials = new FakeSerials(ClayFighter);

        ProductCodeReads.Resolve(GarbledRead() with { Format = ReleaseFormat.Dvd }, "SNS-8C", serials).Format.ShouldBeNull();
        ProductCodeReads.Resolve(GarbledRead() with { Format = ReleaseFormat.GameCartridge }, "SNS-8C", serials).Format
            .ShouldBe(ReleaseFormat.GameCartridge);
    }

    [Fact]
    public void A_read_with_only_a_code_is_still_identified()
    {
        var nothingElse = new ReadFacts(null, null, null, null, null, null, null, null, null);

        var read = ProductCodeReads.Resolve(nothingElse, "SNS-8C-USA", new FakeSerials(ClayFighter));

        read.SearchText.ShouldBe("Clay Fighter");
        read.ReadAs.ShouldBeNull();
        read.ShownText.ShouldBe("Clay Fighter");
    }
}
