using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// The creator swap asks the line the phone sent as the title, with the read's title as the author. On a book that author is a guess
// nobody printed (a neighbour's author, say), and the scorer must know it (ReadItem.CreatorIsAGuess); on a film it is not marked.
public class CreatorSwapGuessTests
{
    private static ReadFacts Read(WorkKind? kind = null, WorkKind? kindContext = null) => new(
        "M.L. EADEN", "M.L. EADEN", null, null, null, kind, null, null, null, KindContext: kindContext, CreatorAsRead: "BEER FOR MY CORPSES");

    [Fact]
    public void The_swap_marks_its_creator_a_guess_on_a_book_read()
    {
        var swapped = CreatorSwap.For(Read(kind: WorkKind.Book));

        swapped.ShouldNotBeNull();
        swapped.CreatorIsAGuess.ShouldBeTrue();
        swapped.Title.ShouldBe("BEER FOR MY CORPSES");
        swapped.Creator.ShouldBe("M.L. EADEN");
        swapped.ToReadItem().CreatorIsAGuess.ShouldBeTrue();
    }

    [Fact]
    public void The_swap_marks_its_creator_a_guess_on_a_read_that_names_no_kind_in_a_snap_of_books()
    {
        var swapped = CreatorSwap.For(Read(kindContext: WorkKind.Book));

        swapped.ShouldNotBeNull();
        swapped.CreatorIsAGuess.ShouldBeTrue();
    }

    [Fact]
    public void The_swap_does_not_mark_its_creator_a_guess_in_a_snap_of_films()
    {
        CreatorSwap.For(Read(kindContext: WorkKind.Film))!.CreatorIsAGuess.ShouldBeFalse();
    }

    [Fact]
    public void The_swap_does_not_mark_its_creator_a_guess_in_a_snap_with_no_kind()
    {
        CreatorSwap.For(Read())!.CreatorIsAGuess.ShouldBeFalse();
    }

    [Theory]
    [InlineData(WorkKind.Film)]
    [InlineData(WorkKind.Tv)]
    [InlineData(WorkKind.Game)]
    public void The_swap_does_not_mark_its_creator_a_guess_on_a_read_of_another_kind(WorkKind kind)
    {
        CreatorSwap.For(Read(kind: kind, kindContext: WorkKind.Book))!.CreatorIsAGuess.ShouldBeFalse();
    }

    [Fact]
    public void A_swap_that_is_not_a_book_is_otherwise_the_same_swap()
    {
        var book = CreatorSwap.For(Read(kindContext: WorkKind.Book))!;
        var film = CreatorSwap.For(Read(kindContext: WorkKind.Film))!;

        book.CreatorIsAGuess.ShouldBeTrue();
        (book with { KindContext = WorkKind.Film, CreatorIsAGuess = false }).ShouldBe(film);
    }

    [Fact]
    public void Only_the_swaps_facts_carry_the_guess_and_the_query_is_the_same_with_and_without_it()
    {
        var original = Read(kindContext: WorkKind.Book);

        var swapped = CreatorSwap.For(original)!;

        original.CreatorIsAGuess.ShouldBeFalse();
        original.ToReadItem().CreatorIsAGuess.ShouldBeFalse();
        swapped.ToQuery().ShouldBe((swapped with { CreatorIsAGuess = false }).ToQuery());
    }
}
