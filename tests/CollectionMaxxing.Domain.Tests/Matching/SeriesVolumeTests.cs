using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Locked tests for the series-volume veto (issue #341, docs/16 §6): no threshold tuning may override it.
// Veto cases land T2 even when scores exceed Matched. Do not weaken, skip or delete.
public class SeriesVolumeTests
{
    [Theory]
    [InlineData("One Piece, Vol. 1", "one piece", "vol 1")]
    [InlineData("One Piece Vol. 2", "one piece", "vol 2")]
    [InlineData("One Piece Volume 10", "one piece", "vol 10")]
    [InlineData("One Piece Box Set 2", "one piece", "boxset 2")]
    [InlineData("Final Fantasy VII", "final fantasy", "7")]
    [InlineData("Final Fantasy 7", "final fantasy", "7")]
    [InlineData("Metroid Prime 2", "metroid prime", "2")]
    [InlineData("Dune: Part Two", "dune", "part two")]
    [InlineData("Harry Potter and the Deathly Hallows: Part 2", "harry potter and the deathly hallows", "part 2")]
    [InlineData("The Wheel of Time Book 1", "wheel of time", "book 1")]
    [InlineData("One Piece Omnibus 1", "one piece", "omnibus 1")]
    [InlineData("Madden NFL 2005", "madden nfl", "2005")]
    [InlineData("Dune", "dune", "")]
    [InlineData("The Matrix", "matrix", "")]
    public void Parse_splits_a_title_into_its_series_and_its_volume(string title, string series, string volume)
    {
        SeriesVolumes.Parse(title).ShouldBe(new SeriesVolume(series, volume));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2005")]
    [InlineData("Vol. 1")]
    public void Parse_finds_no_series_in_a_blank_title_or_a_volume_on_its_own(string? title)
    {
        SeriesVolumes.Parse(title).ShouldBeNull();
    }

    [Theory]
    // The research failure: three series volumes all matched vol. 1.
    [InlineData("One Piece Vol. 1", "One Piece Vol. 2")]
    [InlineData("One Piece, Vol. 1", "One Piece, Vol. 3")]
    [InlineData("Final Fantasy VII", "Final Fantasy VIII")]
    [InlineData("Final Fantasy 7", "Final Fantasy 8")]
    [InlineData("Metroid Prime", "Metroid Prime 2")]
    // An omnibus is not the volume.
    [InlineData("One Piece Vol. 1", "One Piece Omnibus 1")]
    [InlineData("One Piece Omnibus 1", "One Piece Omnibus 2")]
    [InlineData("Naruto Box Set 1", "Naruto Vol. 5")]
    // Annuals: the year is the volume.
    [InlineData("Madden NFL 2005", "Madden NFL 2004")]
    public void Same_series_with_a_different_volume_is_a_veto(string first, string second)
    {
        SeriesVolumes.Vetoes(first, second).ShouldBeTrue();
        SeriesVolumes.Vetoes(second, first).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Dune", "Dune")]
    [InlineData("One Piece Vol. 1", "One Piece Vol. 1")]
    [InlineData("One Piece, Vol. 1", "One Piece Volume 1")]
    [InlineData("One Piece Vol. 1", "Bleach Vol. 1")]
    [InlineData("The Matrix", "The Matrix Reloaded")]
    [InlineData("Dune", "Dune Messiah")]
    [InlineData("Super Mario 64", "Super Mario 64 DS")]
    [InlineData("Mega Man X", "Mega Man 10")]
    [InlineData("One Piece Vol. 1", null)]
    [InlineData(null, "One Piece Vol. 1")]
    [InlineData(null, null)]
    public void Different_series_or_the_same_volume_is_not_a_veto(string? first, string? second)
    {
        SeriesVolumes.Vetoes(first, second).ShouldBeFalse();
        SeriesVolumes.Vetoes(second, first).ShouldBeFalse();
    }

    [Fact]
    public void A_series_title_with_no_volume_against_its_numbered_volume_is_a_veto()
    {
        // The unmarked one might be any volume: the safe direction is to make the person pick.
        SeriesVolumes.Vetoes("One Piece", "One Piece Vol. 1").ShouldBeTrue();
    }

    [Fact]
    public void Years_that_agree_are_not_a_conflict()
    {
        SeriesVolumes.Vetoes("Madden NFL 2005", "Madden NFL 2005").ShouldBeFalse();
    }
}
