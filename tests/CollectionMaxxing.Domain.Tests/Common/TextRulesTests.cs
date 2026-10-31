using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Tests.Common;

public class TextRulesTests
{
    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("\u0000")]
    [InlineData("bell\u0007")]
    [InlineData("escape\u001b[0m")]
    [InlineData("delete\u007f")]
    [InlineData("next line\u0085")]
    public void Text_with_a_control_character_other_than_tab_and_line_breaks_is_refused_naming_the_field(string text)
    {
        var error = TextRules.Check(text, "notes");

        error.ShouldNotBeNull();
        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Code.ShouldBe("text.invalid_character");
        error.Field.ShouldBe("notes");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Plain title")]
    [InlineData("one\ttwo\r\nthree\nfour")]
    [InlineData("Amélie, 千と千尋の神隠し, \U0001F4FC")]
    public void Ordinary_text_is_fine_including_tabs_line_breaks_and_any_script(string? text) =>
        TextRules.Check(text, "notes").ShouldBeNull();
}
