using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Issue #514's work-identity rule: the same novel under several provider work keys
// (Open Library's split "Suddenly You" records) is one novel; a same-title book by
// a different author (Sarah Mayberry's "Suddenly You") is a different work.
public class SameNovelTests
{
    private const string Title = "Suddenly You";
    private const string Author = "Lisa Kleypas";

    [Fact]
    public void Same_title_and_author_across_years_is_the_same_novel()
    {
        SameNovel.IsSame(WorkKind.Book, Title, [Author], WorkKind.Book, Title, [Author]).ShouldBeTrue();
    }

    [Fact]
    public void Title_case_and_punctuation_do_not_matter()
    {
        SameNovel.IsSame(WorkKind.Book, "  SUDDENLY you! ", [Author], WorkKind.Book, Title, [Author]).ShouldBeTrue();
    }

    [Fact]
    public void Author_case_does_not_matter()
    {
        SameNovel.IsSame(WorkKind.Book, Title, ["lisa kleypas"], WorkKind.Book, Title, [Author]).ShouldBeTrue();
    }

    [Fact]
    public void A_different_author_is_a_different_work()
    {
        SameNovel.IsSame(WorkKind.Book, Title, [Author], WorkKind.Book, Title, ["Sarah Mayberry"]).ShouldBeFalse();
    }

    [Fact]
    public void An_extra_author_is_a_different_work()
    {
        SameNovel.IsSame(WorkKind.Book, Title, [Author], WorkKind.Book, Title, [Author, "Someone Else"]).ShouldBeFalse();
    }

    [Fact]
    public void A_different_title_is_a_different_work()
    {
        SameNovel.IsSame(WorkKind.Book, Title, [Author], WorkKind.Book, "Blue-Eyed Devil", [Author]).ShouldBeFalse();
    }

    [Fact]
    public void No_author_on_either_side_is_not_the_same_novel()
    {
        SameNovel.IsSame(WorkKind.Book, Title, [], WorkKind.Book, Title, [Author]).ShouldBeFalse();
        SameNovel.IsSame(WorkKind.Book, Title, [Author], WorkKind.Book, Title, []).ShouldBeFalse();
        SameNovel.IsSame(WorkKind.Book, Title, null, WorkKind.Book, Title, [Author]).ShouldBeFalse();
    }

    [Fact]
    public void A_blank_title_is_not_the_same_novel()
    {
        SameNovel.IsSame(WorkKind.Book, "  ", [Author], WorkKind.Book, "  ", [Author]).ShouldBeFalse();
    }

    [Theory]
    [InlineData(WorkKind.Film)]
    [InlineData(WorkKind.Tv)]
    [InlineData(WorkKind.Game)]
    [InlineData(WorkKind.Other)]
    public void Only_books_are_compared(WorkKind kind)
    {
        SameNovel.IsSame(kind, Title, [Author], kind, Title, [Author]).ShouldBeFalse();
        SameNovel.IsSame(WorkKind.Book, Title, [Author], kind, Title, [Author]).ShouldBeFalse();
    }
}
