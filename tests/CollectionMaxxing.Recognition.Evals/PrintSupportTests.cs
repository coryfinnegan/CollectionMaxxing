using CollectionMaxxing.Application.Evals;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// Whether a shelf's own print backs up a title only the picture model read. These are the phone's cases
/// (<c>printSupport.test.ts</c>) word for word: the server scores the combined row by the same rule, so the two must not drift.
/// </summary>
public sealed class PrintSupportTests
{
    private static IReadOnlySet<string> PrintOf(params string[] lines) => PrintSupport.PrintedWords(lines);

    [Fact]
    public void The_words_of_a_title_are_lowercased_split_on_anything_but_letters_and_digits_with_stop_words_and_short_words_dropped()
    {
        PrintSupport.TitleWords("The Hunt for RED October!").ShouldBe(["hunt", "red", "october"]);
        PrintSupport.TitleWords("Ender's Game: Part 2 - a Sequel").ShouldBe(["ender", "game", "part", "sequel"]);
        PrintSupport.TitleWords("the a an of and to in on for at by with from or is Wonder").ShouldBe(["wonder"]);
        PrintSupport.TitleWords("Fahrenheit 451 and 9").ShouldBe(["fahrenheit", "451"]);
        PrintSupport.TitleWords(null).ShouldBeEmpty();
    }

    [Fact]
    public void The_printed_words_are_every_word_of_every_line_whatever_the_line_is()
    {
        PrintOf("KIMBA THE LION", "Walt-Disney 1994", "  ").OrderBy(w => w, StringComparer.Ordinal).ShouldBe(["1994", "disney", "kimba", "lion", "the", "walt"]);
        PrintOf().ShouldBeEmpty();
    }

    [Fact]
    public void A_title_none_of_whose_words_the_shelf_printed_is_not_supported()
    {
        PrintSupport.Supports("The Matrix", PrintOf("DUNE", "FRANK HERBERT", "EMMA")).ShouldBeFalse();
    }

    [Fact]
    public void A_title_with_two_thirds_of_its_words_printed_is_supported_stop_words_not_counted()
    {
        PrintSupport.Supports("THE FIRST FOR A RED OCTOBER", PrintOf("RED", "OCTOBER")).ShouldBeTrue();
    }

    [Fact]
    public void A_title_with_less_than_two_thirds_of_its_words_printed_is_not_supported()
    {
        PrintSupport.Supports("The Lion, the Witch and the Wardrobe", PrintOf("KIMBA THE LION")).ShouldBeFalse();
    }

    [Fact]
    public void Two_thirds_is_asked_for_exactly_whatever_the_number_of_words()
    {
        const string six = "Alpha Bravo Charlie Delta Echo Foxtrot";
        PrintSupport.Supports(six, PrintOf("alpha", "bravo", "charlie", "delta")).ShouldBeTrue();
        PrintSupport.Supports(six, PrintOf("alpha", "bravo", "charlie")).ShouldBeFalse();
        PrintSupport.Supports("Alpha Bravo Charlie Delta", PrintOf("alpha", "bravo", "charlie")).ShouldBeTrue();
        PrintSupport.Supports("Alpha Bravo Charlie Delta", PrintOf("alpha", "bravo")).ShouldBeFalse();
    }

    [Fact]
    public void A_plural_is_printed_either_way_round_and_only_one_trailing_s_counts()
    {
        PrintSupport.Supports("Queen of Shadow", PrintOf("SHADOWS", "QUEEN")).ShouldBeTrue();
        PrintSupport.Supports("Queen of Shadows", PrintOf("SHADOW", "QUEEN")).ShouldBeTrue();
        PrintSupport.Supports("Shadows", PrintOf("SHADOWING")).ShouldBeFalse();
    }

    [Fact]
    public void A_word_one_edit_away_is_printed_when_both_words_are_at_least_five_letters()
    {
        PrintSupport.Supports("Alchemised", PrintOf("ALCHEMISEO")).ShouldBeTrue(); // substituted
        PrintSupport.Supports("Alchemised", PrintOf("ALCHEMISD")).ShouldBeTrue(); // dropped
        PrintSupport.Supports("Alchemised", PrintOf("ALCHEMISSED")).ShouldBeTrue(); // added
        PrintSupport.Supports("Alchemised", PrintOf("ALCHEMISXO")).ShouldBeFalse(); // two edits
        PrintSupport.Supports("Alchemised", PrintOf("ALCHEMISDE")).ShouldBeFalse(); // two letters swapped is two edits
    }

    [Fact]
    public void Short_words_are_not_read_fuzzily()
    {
        PrintSupport.Supports("Cat", PrintOf("CAR")).ShouldBeFalse();
        PrintSupport.Supports("Dune", PrintOf("DUNK")).ShouldBeFalse();
        PrintSupport.Supports("Emmas", PrintOf("EMMAS")).ShouldBeTrue();
        PrintSupport.Supports("Heart", PrintOf("HEARD")).ShouldBeTrue();
        PrintSupport.Supports("Heart", PrintOf("HEAR")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("The Of A")]
    [InlineData("")]
    [InlineData("!!")]
    [InlineData(null)]
    public void A_title_with_no_words_left_after_the_stop_words_is_not_supported(string? title)
    {
        PrintSupport.Supports(title, PrintOf("THE", "OF", "A")).ShouldBeFalse();
    }

    [Fact]
    public void Nothing_is_supported_when_nothing_is_printed()
    {
        PrintSupport.Supports("Dune", PrintOf()).ShouldBeFalse();
    }

    [Fact]
    public void A_surname_printed_with_its_initials_is_a_printed_name()
    {
        PrintSupport.PrintsAll("V. E. Schwab", PrintOf("V.E. SCHWAB")).ShouldBeTrue();
        PrintSupport.PrintsAll("V. E. Schwab", PrintOf("OF ADDIE LARUE", "V E SCHWAB")).ShouldBeTrue();
    }

    [Fact]
    public void Initials_printed_together_are_printed_initials()
    {
        PrintSupport.PrintsAll("C. M. McGuire", PrintOf("CM.MCQUIRE")).ShouldBeTrue();
    }

    [Fact]
    public void A_surname_printed_without_all_its_initials_is_no_printed_name()
    {
        PrintSupport.PrintsAll("V. E. Schwab", PrintOf("SCHWAB")).ShouldBeFalse();
        PrintSupport.PrintsAll("V. E. Schwab", PrintOf("V SCHWAB")).ShouldBeFalse();
    }

    [Fact]
    public void Initials_printed_without_the_surname_are_no_printed_name()
    {
        PrintSupport.PrintsAll("V. E. Schwab", PrintOf("V.E. SMITH")).ShouldBeFalse();
    }

    [Fact]
    public void Initials_joined_in_another_order_are_no_printed_initials()
    {
        PrintSupport.PrintsAll("C. M. McGuire", PrintOf("MC MCGUIRE")).ShouldBeFalse();
        PrintSupport.PrintsAll("C. M. McGuire", PrintOf("MC.CM MCGUIRE")).ShouldBeTrue();
        PrintSupport.PrintsAll("C. M. McGuire", PrintOf("MC.MC MCGUIRE")).ShouldBeFalse();
    }

    [Fact]
    public void A_lone_word_with_no_initials_is_still_no_printed_name()
    {
        PrintSupport.PrintsAll("Schwab", PrintOf("SCHWAB")).ShouldBeFalse();
        PrintSupport.PrintsAll("Madonna", PrintOf("MADONNA")).ShouldBeFalse();
    }

    [Fact]
    public void A_two_word_name_needs_both_words_and_not_its_initials_as_before()
    {
        PrintSupport.PrintsAll("Amber V. Nicole", PrintOf("AMBER NICOLE")).ShouldBeTrue();
        PrintSupport.PrintsAll("Amber V. Nicole", PrintOf("AMBER")).ShouldBeFalse();
        PrintSupport.PrintsAll("Rachel Gillig", PrintOf("RACHEL GILLIG")).ShouldBeTrue();
        PrintSupport.PrintsAll("Rachel Gillig", PrintOf("GILLIG")).ShouldBeFalse();
    }
}
