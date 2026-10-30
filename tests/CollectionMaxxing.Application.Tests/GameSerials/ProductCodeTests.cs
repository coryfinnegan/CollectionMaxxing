using CollectionMaxxing.Application.GameSerials;

namespace CollectionMaxxing.Application.Tests.GameSerials;

// A cartridge label or a disc prints a product code; the phone reads it as text, so it arrives with whatever separators the
// print or the OCR gave it. The normaliser makes those one spelling, and refuses what is not a code at all.
public class ProductCodeTests
{
    [Theory]
    [InlineData("SNS-ADNE-USA", "SNS-ADNE-USA")]
    [InlineData("sns-adne-usa", "SNS-ADNE-USA")]
    [InlineData("SNS ADNE-USA", "SNS-ADNE-USA")]
    [InlineData("SNS ADNE USA", "SNS-ADNE-USA")]
    [InlineData("SNS_ADNE_USA", "SNS-ADNE-USA")]
    [InlineData("SNS - ADNE - USA", "SNS-ADNE-USA")]
    [InlineData("  SNS--ADNE-USA  ", "SNS-ADNE-USA")]
    [InlineData("SNS–ADNE–USA", "SNS-ADNE-USA")]
    [InlineData("NUS-NSME-USA", "NUS-NSME-USA")]
    [InlineData("SNS-8C", "SNS-8C")]
    public void Separators_and_case_are_unified(string raw, string expected) =>
        ProductCode.Normalize(raw).ShouldBe(expected);

    [Theory]
    [InlineData("SLUS_005.94", "SLUS-00594")]
    [InlineData("SLUS 00594", "SLUS-00594")]
    [InlineData("SLUS-005.94", "SLUS-00594")]
    [InlineData("slus_00594", "SLUS-00594")]
    public void A_playstation_disc_code_loses_the_dot_that_splits_its_digits(string raw, string expected) =>
        ProductCode.Normalize(raw).ShouldBe(expected);

    [Theory]
    [InlineData("SLUS00594", "SLUS-00594")]
    [InlineData("BLUS30001", "BLUS-30001")]
    [InlineData("SS44900", "SS-44900")]
    public void A_code_the_ocr_ran_together_gets_its_separator_back(string raw, string expected) =>
        ProductCode.Normalize(raw).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HELLO")]
    [InlineData("A")]
    [InlineData("12345")]
    [InlineData("123-456")]
    [InlineData("-")]
    [InlineData("SNS@ADNE")]
    [InlineData("SNS-ADNE-USA!")]
    [InlineData("SÄNS-ADNE")]
    [InlineData("ABCDEFGHIJ-1234")]
    [InlineData("AB-123456789")]
    [InlineData("SNS-ADNE-USA-EXTRA-VERY-LONG-CODE-THAT-GOES-ON")]
    [InlineData("A-B")]
    public void Anything_that_does_not_look_like_a_code_is_refused(string? raw) =>
        ProductCode.Normalize(raw).ShouldBeNull();

    [Fact]
    public void A_code_is_at_most_32_characters_as_sent()
    {
        ProductCode.IsAcceptable("SNS-ADNE-USA").ShouldBeTrue();
        ProductCode.IsAcceptable(new string(' ', 30) + "SNS-8C").ShouldBeFalse();
        ProductCode.IsAcceptable("SNS-8C" + new string('A', 30)).ShouldBeFalse();
        ProductCode.IsAcceptable("not a code!").ShouldBeFalse();
        ProductCode.IsAcceptable(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("SNS-8C-USA", "SNS-8C")]
    [InlineData("SNS-ADNE-EUR", "SNS-ADNE")]
    [InlineData("DL-DOL-GALE-USA", "DL-DOL-GALE")]
    [InlineData("SLES-55345-ANZ", "SLES-55345")]
    public void A_trailing_region_suffix_comes_off(string code, string expected) =>
        ProductCode.WithoutSuffix(code).ShouldBe(expected);

    [Theory]
    [InlineData("SNS-8C")]
    [InlineData("SLUS-00594")]
    [InlineData("NES-WO")]
    [InlineData("T-81")]
    public void A_code_with_no_region_suffix_is_left_whole(string code) =>
        ProductCode.WithoutSuffix(code).ShouldBeNull();

    [Fact]
    public void Suffixes_to_try_include_the_regions_of_the_cartridge_and_disc_lists()
    {
        ProductCode.Suffixes.ShouldContain("USA");
        ProductCode.Suffixes.ShouldContain("EUR");
        ProductCode.Suffixes.ShouldContain("JPN");
        ProductCode.Suffixes.ShouldContain("NOE");
        ProductCode.Suffixes.ShouldContain("FRA");
        ProductCode.Suffixes.ShouldContain("ESP");
        ProductCode.Suffixes.ShouldContain("ITA");
        ProductCode.Suffixes.ShouldContain("UKV");
        ProductCode.Suffixes.ShouldContain("AUS");
        ProductCode.Suffixes.ShouldContain("CAN");
    }
}
