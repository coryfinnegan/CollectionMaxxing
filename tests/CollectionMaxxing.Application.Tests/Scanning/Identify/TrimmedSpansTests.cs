using CollectionMaxxing.Application.Scanning.Identify;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A spine read that found nothing is asked again without the stray words at its ends (an author's first name in front of the
// title, OCR junk behind it). These are the spans it tries, in the order it tries them. Neutral titles throughout.
public class TrimmedSpansTests
{
    private static string[] Texts(string? title) => [.. TrimmedSpans.For(title).Select(span => span.Text)];

    [Fact]
    public void A_five_word_read_tries_one_off_the_start_one_off_the_end_one_off_each_end_then_two_off_the_start()
    {
        Texts("SARAH J. CROWN OF MIDNIGHT").ShouldBe(["J. CROWN OF MIDNIGHT", "SARAH J. CROWN OF", "J. CROWN OF", "CROWN OF MIDNIGHT"]);
    }

    [Fact]
    public void Two_off_the_end_comes_last_and_is_cut_by_the_cap_of_four_spans()
    {
        TrimmedSpans.For("SARAH J. CROWN OF MIDNIGHT").Count.ShouldBe(TrimmedSpans.MaxSpans);
        Texts("SARAH J. CROWN OF MIDNIGHT").ShouldNotContain("SARAH J. CROWN");
    }

    [Fact]
    public void A_long_read_still_makes_no_more_than_four_spans()
    {
        TrimmedSpans.For("one two three four five six seven eight").Count.ShouldBe(4);
    }

    [Fact]
    public void Two_off_the_end_is_tried_when_the_floor_or_a_repeat_has_left_room_for_it()
    {
        // 1 off each end leaves "ab", and 2 off the start leaves "cd": both too short. 2 off the end leaves a word of ten letters.
        var spans = TrimmedSpans.For("LONGERWORD ab cd");

        spans.Select(span => span.Text).ShouldBe(["ab cd", "LONGERWORD ab", "LONGERWORD"]);
        spans[2].Dropped.ShouldBe("ab cd");
    }

    [Fact]
    public void The_words_dropped_are_named_in_the_order_they_were_read()
    {
        var spans = TrimmedSpans.For("SARAH J. CROWN OF MIDNIGHT");

        spans[0].Dropped.ShouldBe("SARAH");
        spans[1].Dropped.ShouldBe("MIDNIGHT");
        spans[2].Dropped.ShouldBe("SARAH, MIDNIGHT");
        spans[3].Dropped.ShouldBe("SARAH J.");
    }

    [Fact]
    public void A_stray_word_at_either_end_of_a_four_word_read_is_dropped_by_the_span_that_drops_one_off_each_end()
    {
        Texts("cnoi Emergency Contact o").ShouldContain("Emergency Contact");
    }

    [Fact]
    public void A_span_of_one_word_needs_seven_letters()
    {
        Texts("THO LIBRARIANS XYZ").ShouldContain("LIBRARIANS"); // 2 off the end leaves "THO", 1 off each end leaves "LIBRARIANS"
        Texts("THO LIBRARIANS XYZ").ShouldNotContain("THO");
        Texts("THE CAT SAT").ShouldBe(["CAT SAT", "THE CAT"]); // "CAT", "THE" and "SAT" are too short to find anything
    }

    [Fact]
    public void A_one_word_span_counts_letters_not_digits_or_punctuation()
    {
        Texts("xx 1234567 yy").ShouldBe(["1234567 yy", "xx 1234567"]);
        Texts("xx ABC-DEF yy").ShouldNotContain("ABC-DEF"); // six letters
        Texts("xx ABCD-EFG yy").ShouldContain("ABCD-EFG"); // seven letters
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TNX")]
    [InlineData("TNX EVERYONE")]
    [InlineData("  TNX   EVERYONE  ")]
    public void A_read_of_fewer_than_three_words_is_never_retried(string? title)
    {
        TrimmedSpans.For(title).ShouldBeEmpty();
    }

    [Fact]
    public void A_span_is_never_tried_twice_whatever_its_case()
    {
        Texts("ab ab ab ab").ShouldBe(["ab ab ab", "ab ab"]);
        Texts("Ab ab AB aB").ShouldBe(["ab AB aB", "ab AB"]); // "Ab ab AB" is "ab AB aB" again, and "AB aB" and "Ab ab" are "ab AB"
    }

    [Fact]
    public void Extra_spaces_between_words_do_not_change_the_spans()
    {
        Texts("  SARAH   J.  CROWN   OF  MIDNIGHT ").ShouldBe(Texts("SARAH J. CROWN OF MIDNIGHT"));
    }

    [Fact]
    public void A_span_that_is_only_an_edition_phrase_is_never_tried()
    {
        // #815: "HIGHLANDER DIRECTOR'S CUT" without its first word is an edition, not a title.
        Texts("HIGHLANDER DIRECTOR'S CUT").ShouldBe(["HIGHLANDER DIRECTOR'S", "DIRECTOR'S", "HIGHLANDER"]);
    }

    // #815: a span's find must carry the read's leading words (asked for a read that names its kind).
    [Theory]
    [InlineData("HIGHLANDER THE FINAL DIMENSION", 0, "The Final Destination", "", false)] // the franchise word dropped and not carried
    [InlineData("HIGHLANDER THE FINAL DIMENSION", 1, "The Final", "", false)] // kept by the span, still not carried by the find
    [InlineData("HIGHLANDER THE FINAL DIMENSION", 0, "Highlander III: The Final Dimension", "", true)]
    [InlineData("HIGHLANDER THE FINAL DIMENSION", 1, "Highlander", "", true)]
    [InlineData("SARAH J. CROWN OF MIDNIGHT", 3, "Crown of Midnight", "Sarah J. Maas", true)] // the author's name is carried by the creator
    [InlineData("SARAH J. CROWN OF MIDNIGHT", 3, "Crown of Midnight", "Tess Harrowgate", false)]
    [InlineData("THE COPPER LANTERN ROAD", 0, "Copper Lantern Road", "", true)] // a stop word asks nothing
    [InlineData("7 COPPER LANTERN ROAD", 0, "Copper Lantern Road", "", true)] // nor does a sticker number
    [InlineData("A J COPPER LANTERN", 0, "Copper Lantern", "", true)] // nor an initial
    public void A_spans_find_carries_the_reads_leading_words_or_does_not(string read, int span, string title, string creator, bool carries)
    {
        var spans = TrimmedSpans.For(read);

        TrimmedSpans.FindCarriesLeadingWords(read, spans[span], title, creator.Length > 0 ? [creator] : []).ShouldBe(carries);
    }

    [Fact]
    public void Each_span_says_which_words_its_start_lost()
    {
        TrimmedSpans.For("SARAH J. CROWN OF MIDNIGHT").Select(span => span.DroppedStart).ShouldBe(["SARAH", "", "SARAH", "SARAH J."]);
    }
}
