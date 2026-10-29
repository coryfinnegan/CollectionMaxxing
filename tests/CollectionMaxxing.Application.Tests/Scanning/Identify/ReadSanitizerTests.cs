using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// Reads the on-device model really sent (owner's server, 2026-10-24 to 10-29): enum words in the platform, a loose-cart
// completeness on a book cover, ratings and bestseller lines as the edition.
public class ReadSanitizerTests
{
    private static ReadFacts Read(
        string title = "Suddenly You",
        WorkKind? kind = WorkKind.Book,
        string? platform = null,
        ItemCompleteness? completeness = null,
        string? edition = null,
        ReleaseFormat? format = null,
        string? creator = null) =>
        new(title, title, creator, edition, completeness, kind, format, null, platform);

    [Theory]
    [InlineData("book")]
    [InlineData("boxed")]
    [InlineData("looseCart")]
    [InlineData("manualVisible")]
    public void A_book_cover_loses_the_platform_and_completeness_the_model_guessed(string platform)
    {
        var clean = ReadSanitizer.Sanitize(Read(platform: platform, completeness: ItemCompleteness.CartOnly));

        clean.Platform.ShouldBeNull();
        clean.Completeness.ShouldBeNull();
        clean.Title.ShouldBe("Suddenly You");
    }

    [Theory]
    [InlineData("boxed")]
    [InlineData("discOnly")]
    [InlineData("game")]
    [InlineData("DVD")]
    public void A_game_platform_that_names_no_console_is_dropped(string platform)
    {
        ReadSanitizer.Sanitize(Read("Super Mario 64", WorkKind.Game, platform)).Platform.ShouldBeNull();
    }

    [Theory]
    [InlineData("NINTENDO SWITCH")]
    [InlineData("Atari 2600")]
    [InlineData("Neo-Geo CD")]
    public void A_game_platform_that_may_be_a_console_is_kept(string platform)
    {
        ReadSanitizer.Sanitize(Read("Some Game", WorkKind.Game, platform)).Platform.ShouldBe(platform);
    }

    [Fact]
    public void A_disc_read_as_a_film_that_names_a_console_is_a_game_disc()
    {
        // A PS2 game comes in a DVD case; the model can call it a dvd while reading the console off the spine.
        var clean = ReadSanitizer.Sanitize(Read("Shadow of the Colossus", WorkKind.Film, "PlayStation 2", format: ReleaseFormat.Dvd));

        clean.Kind.ShouldBe(WorkKind.Game);
        clean.Format.ShouldBe(ReleaseFormat.GameDisc);
        clean.Platform.ShouldBe("PlayStation 2");
    }

    [Fact]
    public void A_film_platform_that_names_no_console_is_dropped()
    {
        ReadSanitizer.Sanitize(Read("The Matrix", WorkKind.Film, "Warner", format: ReleaseFormat.Dvd)).Platform.ShouldBeNull();
    }

    [Fact]
    public void A_kind_of_other_is_the_reader_not_knowing()
    {
        ReadSanitizer.Sanitize(Read(kind: WorkKind.Other)).Kind.ShouldBeNull();
    }

    [Theory]
    [InlineData("NEW YORK TIMES BESTSELLING AUTHOR")]
    [InlineData("#1 New York Times Bestseller")]
    [InlineData("ISBN 0-00-000000-0 9780000000002")]
    [InlineData("E10+")]
    [InlineData("T")]
    [InlineData("PG-13")]
    [InlineData("Rated R")]
    [InlineData("WINNER OF THE HUGO AWARD")]
    [InlineData("A Novel")]
    [InlineData("Now a major motion picture")]
    public void An_edition_that_is_a_rating_a_tagline_or_an_isbn_is_dropped(string edition)
    {
        ReadSanitizer.Sanitize(Read(edition: edition)).Edition.ShouldBeNull();
    }

    [Theory]
    [InlineData("Player's Choice")]
    [InlineData("Greatest Hits")]
    [InlineData("Criterion Collection")]
    [InlineData("Unrated")]
    [InlineData("10th Anniversary Edition")]
    [InlineData("Collector's Edition")]
    public void A_real_edition_mark_is_kept(string edition)
    {
        ReadSanitizer.Sanitize(Read(edition: edition)).Edition.ShouldBe(edition);
    }

    [Fact]
    public void A_bare_tape_keeps_its_loose_completeness_but_a_dvd_cannot_be_a_loose_cart()
    {
        ReadSanitizer.Sanitize(Read("Jaws", WorkKind.Film, completeness: ItemCompleteness.CartOnly, format: ReleaseFormat.Vhs))
            .Completeness.ShouldBe(ItemCompleteness.CartOnly);
        ReadSanitizer.Sanitize(Read("Jaws", WorkKind.Film, completeness: ItemCompleteness.CartOnly, format: ReleaseFormat.Dvd))
            .Completeness.ShouldBeNull();
    }

    [Fact]
    public void A_book_is_never_scored_as_a_loose_cartridge_or_a_bare_label()
    {
        ScoringProfileSelector.Choose(ItemCompleteness.CartOnly, WorkKind.Book, null, 1).ShouldBe(ScoringProfileKind.Cover);
        ScoringProfileSelector.Choose(ItemCompleteness.DiscOnly, WorkKind.Book, null, 1).ShouldBe(ScoringProfileKind.Cover);
    }

    // ---- the creator ------------------------------------------------------------------------------
    // Fourth eval run: the model filled the author with the series line or brand off the spine (a series' name for a
    // book of that series) and with placeholders ("the authors"). Scored against the real author, that sank exact
    // titles under the Matched line; asked of the providers, it found nothing.

    [Theory]
    [InlineData("The Lantern Road Cookbook", "Lantern Road")]
    [InlineData("THE Lantern COOKBOOK", "THE Lantern")]
    [InlineData("lantern road cookbook", "LANTERN ROAD COOKBOOK")]
    public void A_creator_that_only_repeats_words_of_the_title_is_dropped(string title, string creator)
    {
        ReadSanitizer.Sanitize(Read(title, creator: creator)).Creator.ShouldBeNull();
    }

    [Theory]
    [InlineData("The Lantern Road Cookbook", "Mara Odell")]
    [InlineData("ODELL lantern road cookbook", "Mara Odell")]
    [InlineData("Mara Odell's Lantern Road", "Mara Odell")]
    [InlineData("Lantern Road", "The Editors of Lantern Magazine")]
    public void A_creator_with_a_word_the_title_lacks_is_kept(string title, string creator)
    {
        ReadSanitizer.Sanitize(Read(title, creator: creator)).Creator.ShouldBe(creator);
    }

    [Theory]
    [InlineData("the authors")]
    [InlineData("Author")]
    [InlineData("Unknown")]
    [InlineData("various authors")]
    [InlineData("N/A")]
    public void A_placeholder_creator_is_dropped(string creator)
    {
        ReadSanitizer.Sanitize(Read(creator: creator)).Creator.ShouldBeNull();
    }

    // ---- what the shot was --------------------------------------------------------------------

    [Fact]
    public void A_shelf_shot_that_read_one_item_is_a_cover_shot()
    {
        // The gate fires for every auto snap, so a cover held up to the camera is framed as a shelf; one item is a cover.
        ShotReading.Of(ShotKind.ShelfSegment, itemsRead: 1).ShouldBe(ShotKind.SingleCover);
    }

    [Fact]
    public void A_shelf_shot_that_read_several_items_stays_a_shelf_and_other_shots_keep_their_kind()
    {
        ShotReading.Of(ShotKind.ShelfSegment, itemsRead: 3).ShouldBe(ShotKind.ShelfSegment);
        ShotReading.Of(ShotKind.Unknown, itemsRead: 1).ShouldBe(ShotKind.Unknown); // already compares artwork
        ShotReading.Of(null, itemsRead: 1).ShouldBe(ShotKind.Unknown);
        ShotReading.Of(null, itemsRead: 3).ShouldBe(ShotKind.Unknown);
        ShotReading.Of(ShotKind.LooseItem, itemsRead: 1).ShouldBe(ShotKind.LooseItem);
        ShotReading.Of(ShotKind.SingleCover, itemsRead: 1).ShouldBe(ShotKind.SingleCover);
    }

    // ---- the read format travels to the release -------------------------------------------------

    private static ProviderRelease Film(ReleaseFormat? format = null, WorkKind kind = WorkKind.Film) => new()
    {
        Provider = "tmdb",
        ExternalId = "movie/603",
        Title = "The Matrix",
        Kind = kind,
        Format = format,
    };

    [Theory]
    [InlineData(ReleaseFormat.Vhs)]
    [InlineData(ReleaseFormat.Dvd)]
    [InlineData(ReleaseFormat.Bluray)]
    [InlineData(ReleaseFormat.UhdBluray)]
    public void A_film_with_no_format_takes_the_format_the_cover_was_read_as(ReleaseFormat read)
    {
        Film().WithReadFormat(read).Format.ShouldBe(read);
        Film(ReleaseFormat.Unknown).WithReadFormat(read).Format.ShouldBe(read);
        Film(kind: WorkKind.Tv).WithReadFormat(read).Format.ShouldBe(read);
    }

    [Fact]
    public void A_release_that_names_its_own_format_keeps_it_and_a_misfit_format_is_not_stamped()
    {
        Film(ReleaseFormat.Bluray).WithReadFormat(ReleaseFormat.Dvd).Format.ShouldBe(ReleaseFormat.Bluray);
        Film().WithReadFormat(ReleaseFormat.Paperback).Format.ShouldBeNull();
        Film(kind: WorkKind.Game).WithReadFormat(ReleaseFormat.Dvd).Format.ShouldBeNull();
        Film().WithReadFormat(null).Format.ShouldBeNull();
    }
}
