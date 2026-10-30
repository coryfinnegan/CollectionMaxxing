using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

/// <summary>
/// The kind a snap's reads agree on, when they clearly agree: at least three reads that name a kind, and at least 70% of them
/// naming the same one (a film and a show are one family). Anything less is no context at all.
/// </summary>
public class KindContextTests
{
    private static WorkKind? Dominant(params WorkKind?[] kinds) => KindContext.Dominant(kinds);

    [Fact]
    public void Three_reads_that_name_one_kind_make_it_the_snaps_kind()
    {
        Dominant(WorkKind.Book, WorkKind.Book, WorkKind.Book).ShouldBe(WorkKind.Book);
        Dominant(WorkKind.Game, WorkKind.Game, WorkKind.Game).ShouldBe(WorkKind.Game);
    }

    [Fact]
    public void Two_reads_are_a_coincidence_not_a_shelf()
    {
        Dominant(WorkKind.Film, WorkKind.Film).ShouldBeNull();
        Dominant(WorkKind.Film).ShouldBeNull();
        Dominant().ShouldBeNull();
    }

    [Fact]
    public void One_odd_read_out_of_four_does_not_break_the_majority()
    {
        Dominant(WorkKind.Film, WorkKind.Film, WorkKind.Film, WorkKind.Book).ShouldBe(WorkKind.Film);
    }

    [Fact]
    public void A_split_shelf_has_no_kind()
    {
        Dominant(WorkKind.Film, WorkKind.Film, WorkKind.Book, WorkKind.Book).ShouldBeNull();
    }

    [Fact]
    public void Three_of_five_is_not_a_clear_majority_and_four_of_five_is()
    {
        Dominant(WorkKind.Book, WorkKind.Book, WorkKind.Book, WorkKind.Film, WorkKind.Film).ShouldBeNull();
        Dominant(WorkKind.Book, WorkKind.Book, WorkKind.Book, WorkKind.Book, WorkKind.Film).ShouldBe(WorkKind.Book);
    }

    [Fact]
    public void Two_of_three_is_not_enough_because_three_reads_must_all_agree()
    {
        Dominant(WorkKind.Book, WorkKind.Book, WorkKind.Film).ShouldBeNull();
    }

    [Fact]
    public void Exactly_seventy_percent_is_enough()
    {
        var seven = Enumerable.Repeat<WorkKind?>(WorkKind.Game, 7).Concat(Enumerable.Repeat<WorkKind?>(WorkKind.Book, 3));

        KindContext.Dominant(seven).ShouldBe(WorkKind.Game);
    }

    [Fact]
    public void Just_under_seventy_percent_is_not()
    {
        var nineOfThirteen = Enumerable.Repeat<WorkKind?>(WorkKind.Game, 9).Concat(Enumerable.Repeat<WorkKind?>(WorkKind.Book, 4)); // 69.2%

        KindContext.Dominant(nineOfThirteen).ShouldBeNull();
    }

    [Fact]
    public void A_film_and_a_show_are_one_family_and_the_family_is_reported_as_film()
    {
        Dominant(WorkKind.Film, WorkKind.Tv, WorkKind.Tv).ShouldBe(WorkKind.Film);
        Dominant(WorkKind.Tv, WorkKind.Tv, WorkKind.Tv).ShouldBe(WorkKind.Film);
        Dominant(WorkKind.Film, WorkKind.Tv, WorkKind.Film, WorkKind.Tv, WorkKind.Book).ShouldBe(WorkKind.Film);
    }

    [Fact]
    public void Reads_that_name_no_kind_say_nothing_either_way()
    {
        Dominant(WorkKind.Book, WorkKind.Book, WorkKind.Book, null, null, null, null, WorkKind.Other).ShouldBe(WorkKind.Book);
        Dominant(WorkKind.Book, WorkKind.Book, null, null, null).ShouldBeNull();
        Dominant(null, null, null, WorkKind.Other, WorkKind.Other).ShouldBeNull();
    }

    [Theory]
    [InlineData(WorkKind.Film, WorkKind.Film, true)]
    [InlineData(WorkKind.Tv, WorkKind.Film, true)]
    [InlineData(WorkKind.Film, WorkKind.Tv, true)]
    [InlineData(WorkKind.Book, WorkKind.Film, false)]
    [InlineData(WorkKind.Game, WorkKind.Film, false)]
    [InlineData(WorkKind.Book, WorkKind.Book, true)]
    [InlineData(WorkKind.Other, WorkKind.Book, false)]
    public void A_release_is_in_the_snaps_family_when_its_kind_is_the_same_or_film_and_show(WorkKind release, WorkKind snap, bool expected)
    {
        KindContext.InFamily(release, snap).ShouldBe(expected);
    }
}
