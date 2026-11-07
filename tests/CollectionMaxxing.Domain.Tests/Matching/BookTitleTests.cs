using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Google Books runs a series marker into a title and carries a marketing tagline as a subtitle; no spine prints either,
// so the title is offered without them. A short real subtitle stays, and a title that is only a marker keeps its words.
public class BookTitleTests
{
    [Theory]
    [InlineData("Harbor Light (Maple Hollow, Book 1)", "Harbor Light")]
    [InlineData("Harbor Light (Maple Hollow #3)", "Harbor Light")]
    [InlineData("Harbor Light (Maple Hollow, #3)", "Harbor Light")]
    [InlineData("Harbor Light (Maple Hollow, book 12)", "Harbor Light")]
    public void A_trailing_series_marker_is_dropped_from_the_title(string title, string expected)
    {
        BookTitle.Clean(title, null).ShouldBe(expected);
    }

    // Volumes of one work share a title, so their marker is what tells them apart.
    [Theory]
    [InlineData("Harbor Light (Book 2)")]
    [InlineData("Les Misérables (Vol. 2)")]
    [InlineData("Harbor Light (#2)")]
    [InlineData("Harbor Light (Some Series, Vol. 2)")]
    [InlineData("Harbor Light (Book 2) Special Edition")]
    public void A_marker_that_names_no_series_or_is_not_last_stays(string title)
    {
        BookTitle.Clean(title, null).ShouldBe(title);
    }

    [Fact]
    public void A_stripped_title_has_no_trailing_space()
    {
        BookTitle.Clean("Harbor Light   (Maple Hollow, Book 1)  ", null).ShouldBe("Harbor Light");
    }

    [Fact]
    public void A_subtitle_of_seven_words_stays_and_one_of_eight_is_dropped()
    {
        BookTitle.Clean("Harbor Light", "one two three four five six seven").ShouldBe("Harbor Light: one two three four five six seven");
        BookTitle.Clean("Harbor Light", "one two three four five six seven eight").ShouldBe("Harbor Light");
    }

    [Theory]
    [InlineData("Hoe sterker de band, hoe groter het gevaar voor de kleine gemeenschap")]
    [InlineData("Keepers | The Coast")]
    [InlineData("Keepers » The Coast")]
    [InlineData("The long wait...")]
    [InlineData("The long wait…")]
    public void A_subtitle_that_is_a_tagline_is_not_part_of_the_title(string subtitle)
    {
        BookTitle.Clean("Harbor Light", subtitle).ShouldBe("Harbor Light");
    }

    [Theory]
    [InlineData("Echoes", "Metroid Prime: Echoes")]
    [InlineData("Keepers of the Coast", "Metroid Prime: Keepers of the Coast")]
    [InlineData("A Cookbook for Slow Sundays", "Metroid Prime: A Cookbook for Slow Sundays")]
    public void A_short_real_subtitle_stays(string subtitle, string expected)
    {
        BookTitle.Clean("Metroid Prime", subtitle).ShouldBe(expected);
    }

    [Fact]
    public void A_genre_label_subtitle_is_still_dropped()
    {
        BookTitle.Clean("Dune", "A Novel").ShouldBe("Dune");
    }

    [Theory]
    [InlineData("(Book 1)")]
    [InlineData("(Maple Hollow, Book 1)")]
    public void A_title_that_is_only_a_series_marker_keeps_its_words(string title)
    {
        BookTitle.Clean(title, null).ShouldBe(title);
    }
}
