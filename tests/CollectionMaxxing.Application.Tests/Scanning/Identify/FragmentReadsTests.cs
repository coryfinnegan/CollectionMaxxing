using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A spine read that only found itself is skipped: the print that is never a title (a number, a format or banner phrase, a
// publisher's mark another spine also prints) and an author's name whose best release is titled with that same name. Neutral
// names throughout.
public class FragmentReadsTests
{
    private static FragmentRead Spine(
        string? title,
        string? printed = null,
        ResolutionVerdict verdict = ResolutionVerdict.Ambiguous,
        string? bestTitle = "Some Other Book",
        string[]? bestCreators = null,
        bool kindless = true) =>
        new(title, printed ?? title, kindless, verdict, bestTitle, bestCreators ?? []);

    [Fact]
    public void A_read_of_a_publishers_mark_printed_on_several_spines_is_skipped()
    {
        var reads = new[]
        {
            Spine("PENGUIN"),
            Spine("The Copper Lantern", printed: "THE COPPER LANTERN PENGUIN"),
            Spine("Quiet Harbor", printed: "QUIET HARBOR Penguin"),
        };

        FragmentReads.Of(reads).ShouldBe([0]);
    }

    [Fact]
    public void A_one_word_title_that_is_also_a_studio_name_is_kept_when_no_other_spine_prints_it()
    {
        var reads = new[] { Spine("Penguin"), Spine("The Copper Lantern"), Spine("Quiet Harbor") };

        FragmentReads.Of(reads).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Universal")]
    [InlineData("Network")]
    public void A_single_generic_word_is_never_a_publishers_mark_even_when_another_spine_prints_it(string word)
    {
        var reads = new[] { Spine(word), Spine("The Copper Lantern", printed: $"THE COPPER LANTERN {word}") };

        FragmentReads.Of(reads).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("VHS")]
    [InlineData("DVD")]
    [InlineData("Blu-ray")]
    [InlineData("HOME VIDEO")]
    [InlineData("Family Entertainment")]
    [InlineData("Director's Cut")]
    [InlineData("Special Edition")]
    [InlineData("25th Anniversary Edition")]
    [InlineData("WIDESCREEN")]
    [InlineData("New York Times Bestselling Author")]
    public void A_read_of_a_format_word_is_skipped(string title)
    {
        FragmentReads.Of([Spine(title)]).ShouldBe([0]);
    }

    [Fact]
    public void A_read_of_digits_only_is_skipped()
    {
        FragmentReads.Of([Spine("0451")]).ShouldBe([0]);
    }

    [Fact]
    public void A_title_that_merely_contains_a_format_word_is_kept()
    {
        FragmentReads.Of([Spine("The Last Video Store"), Spine("Special Forces Handbook")]).ShouldBeEmpty();
    }

    [Fact]
    public void Only_an_ambiguous_kindless_spine_read_is_a_fragment()
    {
        var reads = new[]
        {
            Spine("VHS", verdict: ResolutionVerdict.NotFound),
            Spine("VHS", verdict: ResolutionVerdict.Matched),
            Spine("VHS", kindless: false),
        };

        FragmentReads.Of(reads).ShouldBeEmpty();
    }

    [Fact]
    public void An_author_name_read_whose_best_release_is_titled_with_that_name_is_skipped_when_the_name_is_its_own_creator()
    {
        var reads = new[] { Spine("Tess Harrowgate", bestTitle: "Tess Harrowgate", bestCreators: ["Tess Harrowgate"]) };

        FragmentReads.Of(reads).ShouldBe([0]);
    }

    [Fact]
    public void An_author_name_read_is_skipped_when_every_word_is_in_a_creator_another_answer_carries()
    {
        var reads = new[]
        {
            Spine("Tess Harrowgate", bestTitle: "Tess Harrowgate Story"),
            Spine("The Copper Lantern", bestTitle: "The Copper Lantern", bestCreators: ["Tess Harrowgate"], verdict: ResolutionVerdict.Matched),
        };

        FragmentReads.Of(reads).ShouldBe([0]);
    }

    [Fact]
    public void A_name_one_edit_away_from_a_creator_another_answer_carries_is_still_that_name()
    {
        var reads = new[]
        {
            Spine("JENNIFER ARMBRUSTOR", bestTitle: "Jennifer Armbrustor"),
            Spine("From Ember and Ash", bestTitle: "From Ember and Ash", bestCreators: ["Jennifer Armbruster"], verdict: ResolutionVerdict.Matched),
        };

        FragmentReads.Of(reads).ShouldBe([0]);
    }

    [Fact]
    public void An_author_name_read_whose_answer_is_a_different_title_is_kept()
    {
        var reads = new[]
        {
            Spine("Tess Harrowgate", bestTitle: "The Copper Lantern", bestCreators: ["Tess Harrowgate"]),
            Spine("Quiet Harbor", bestTitle: "Quiet Harbor", bestCreators: ["Tess Harrowgate"], verdict: ResolutionVerdict.Matched),
        };

        FragmentReads.Of(reads).ShouldBeEmpty();
    }

    [Fact]
    public void A_title_that_is_named_like_its_best_release_but_is_nobodys_name_is_kept()
    {
        var reads = new[]
        {
            Spine("Copper Lantern", bestTitle: "Copper Lantern", bestCreators: ["Tess Harrowgate"]),
            Spine("Quiet Harbor", bestTitle: "Quiet Harbor", bestCreators: ["Iris Fenwick"], verdict: ResolutionVerdict.Matched),
        };

        FragmentReads.Of(reads).ShouldBeEmpty();
    }

    [Fact]
    public void A_biography_titled_with_a_name_is_kept_when_no_other_answer_carries_that_name()
    {
        var reads = new[] { Spine("Tess Harrowgate", bestTitle: "Tess Harrowgate", bestCreators: ["Iris Fenwick"]) };

        FragmentReads.Of(reads).ShouldBeEmpty();
    }
}
