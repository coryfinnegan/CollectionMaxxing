using CollectionMaxxing.Infrastructure.ShelfReading;
using ImageMagick;
using Shouldly;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.ShelfReading;

/// <summary>The still maker, on pictures drawn in the test (a flat red/blue pattern, a few KB). No owner photo, no download.</summary>
public sealed class ShelfStillMakerTests
{
    private readonly ShelfStillMaker _maker = new(Microsoft.Extensions.Options.Options.Create(new ShelfReaderOptions()));

    /// <summary>Left half red, right half blue.</summary>
    private static MagickImage Drawn(uint width, uint height)
    {
        var image = new MagickImage(MagickColors.Red, width, height);
        using var blue = new MagickImage(MagickColors.Blue, width / 2, height);
        image.Composite(blue, (int)(width - width / 2), 0, CompositeOperator.Over);
        return image;
    }

    /// <summary>Puts an EXIF "orientation 6" (RightTop) APP1 segment right after the JPEG's start marker.</summary>
    private static byte[] WithSidewaysFlag(byte[] jpeg)
    {
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    private static byte[] Encode(MagickImage image, MagickFormat format)
    {
        image.Format = format;
        return image.ToByteArray();
    }

    private static bool IsJpeg(byte[] bytes) => bytes is [0xFF, 0xD8, 0xFF, ..];

    [Fact]
    public void A_large_jpeg_is_shrunk_to_the_long_edge_and_stays_a_jpeg_without_metadata()
    {
        using var source = Drawn(3000, 1500);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Software, "a camera");
        source.SetProfile(exif);
        var bytes = Encode(source, MagickFormat.Jpeg);

        var still = _maker.Make(bytes);

        still.ShouldNotBeNull();
        IsJpeg(still).ShouldBeTrue();
        using var result = new MagickImage(still);
        result.Width.ShouldBe(2048u);
        result.Height.ShouldBe(1024u);
        result.GetExifProfile().ShouldBeNull();
        result.GetProfile("icc").ShouldBeNull();
    }

    [Fact]
    public void A_small_jpeg_is_not_enlarged()
    {
        using var source = Drawn(300, 200);

        var still = _maker.Make(Encode(source, MagickFormat.Jpeg));

        still.ShouldNotBeNull();
        using var result = new MagickImage(still);
        (result.Width, result.Height).ShouldBe((300u, 200u));
    }

    [Fact]
    public void A_sideways_exif_orientation_is_applied_to_the_pixels()
    {
        using var source = Drawn(400, 200);
        var bytes = WithSidewaysFlag(Encode(source, MagickFormat.Jpeg)); // shown turned a quarter clockwise
        using (var check = new MagickImage(bytes))
        {
            check.Orientation.ShouldBe(ImageMagick.Orientation.RightTop); // the fixture really is sideways
        }

        var still = _maker.Make(bytes);

        still.ShouldNotBeNull();
        using var result = new MagickImage(still);
        (result.Width, result.Height).ShouldBe((200u, 400u));
        result.Orientation.ShouldBeOneOf(ImageMagick.Orientation.Undefined, ImageMagick.Orientation.TopLeft);
        using var pixels = result.GetPixels();
        var top = pixels.GetPixel(100, 20)!.ToColor()!;
        var bottom = pixels.GetPixel(100, 380)!.ToColor()!;
        (top.R > top.B).ShouldBeTrue("the red half is now on top");
        (bottom.B > bottom.R).ShouldBeTrue("the blue half is now at the bottom");
    }

    [Fact]
    public void A_png_becomes_a_jpeg()
    {
        using var source = Drawn(120, 80);

        var still = _maker.Make(Encode(source, MagickFormat.Png));

        still.ShouldNotBeNull();
        IsJpeg(still).ShouldBeTrue();
    }

    /// <summary>
    /// The smallest honest HEIC check: this Magick build cannot write HEIC (no encode delegate) and no HEIC fixture may be
    /// downloaded, so a real HEIC cannot be decoded here. This only proves the decoder is compiled in on this platform.
    /// </summary>
    [Fact]
    public void The_heic_decoder_is_compiled_in()
    {
        MagickNET.SupportedFormats.Single(f => f.Format == MagickFormat.Heic).SupportsReading.ShouldBeTrue();
    }

    [Fact]
    public void A_file_that_is_not_a_picture_is_refused()
    {
        _maker.Make("this is not a picture"u8.ToArray()).ShouldBeNull();
        _maker.Make([]).ShouldBeNull();
    }
}
