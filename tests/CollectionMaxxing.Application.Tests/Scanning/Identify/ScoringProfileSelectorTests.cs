using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

public class ScoringProfileSelectorTests
{
    [Fact]
    public void A_loose_cart_read_uses_the_loose_cartridge_profile()
    {
        ScoringProfileSelector.Choose(ItemCompleteness.CartOnly, WorkKind.Game, ReleaseFormat.GameCartridge, 1)
            .ShouldBe(ScoringProfileKind.LooseCartridge);
    }

    [Fact]
    public void A_cart_read_with_no_stated_completeness_is_still_a_loose_cartridge()
    {
        ScoringProfileSelector.Choose(null, WorkKind.Game, ReleaseFormat.GameCartridge, 1).ShouldBe(ScoringProfileKind.LooseCartridge);
    }

    [Fact]
    public void A_boxed_game_uses_the_cover_profile()
    {
        ScoringProfileSelector.Choose(ItemCompleteness.Complete, WorkKind.Game, ReleaseFormat.GameCartridge, 1).ShouldBe(ScoringProfileKind.Cover);
        ScoringProfileSelector.Choose(ItemCompleteness.BoxOnly, WorkKind.Game, ReleaseFormat.GameDisc, 1).ShouldBe(ScoringProfileKind.Cover);
    }

    [Fact]
    public void A_shelf_of_twelve_book_spines_uses_the_shelf_spine_profile()
    {
        ScoringProfileSelector.Choose(null, WorkKind.Book, ReleaseFormat.Hardcover, 12).ShouldBe(ScoringProfileKind.ShelfSpine);
        ScoringProfileSelector.Choose(null, WorkKind.Book, null, 12).ShouldBe(ScoringProfileKind.ShelfSpine);
    }

    [Fact]
    public void A_single_book_cover_uses_the_cover_profile()
    {
        ScoringProfileSelector.Choose(null, WorkKind.Book, null, 1).ShouldBe(ScoringProfileKind.Cover);
    }

    [Fact]
    public void A_bare_tape_label_uses_the_bare_label_profile()
    {
        ScoringProfileSelector.Choose(ItemCompleteness.CartOnly, WorkKind.Film, ReleaseFormat.Vhs, 1).ShouldBe(ScoringProfileKind.BareLabel);
        ScoringProfileSelector.Choose(ItemCompleteness.DiscOnly, WorkKind.Film, ReleaseFormat.Vhs, 1).ShouldBe(ScoringProfileKind.BareLabel);
    }

    [Fact]
    public void A_bare_disc_uses_the_bare_label_profile()
    {
        ScoringProfileSelector.Choose(ItemCompleteness.DiscOnly, WorkKind.Game, ReleaseFormat.GameDisc, 1).ShouldBe(ScoringProfileKind.BareLabel);
    }

    [Fact]
    public void A_pile_of_loose_carts_is_still_loose_cartridges_not_a_shelf()
    {
        ScoringProfileSelector.Choose(ItemCompleteness.CartOnly, WorkKind.Game, ReleaseFormat.GameCartridge, 8)
            .ShouldBe(ScoringProfileKind.LooseCartridge);
    }

    [Fact]
    public void A_sleeve_or_a_dvd_cover_uses_the_cover_profile()
    {
        ScoringProfileSelector.Choose(ItemCompleteness.Complete, WorkKind.Film, ReleaseFormat.Vhs, 1).ShouldBe(ScoringProfileKind.Cover);
        ScoringProfileSelector.Choose(null, WorkKind.Film, ReleaseFormat.Dvd, 2).ShouldBe(ScoringProfileKind.Cover);
    }

    [Fact]
    public void Three_items_are_not_yet_a_shelf()
    {
        ScoringProfileSelector.Choose(null, WorkKind.Book, null, 3).ShouldBe(ScoringProfileKind.Cover);
        ScoringProfileSelector.Choose(null, WorkKind.Book, null, 4).ShouldBe(ScoringProfileKind.ShelfSpine);
    }
}
