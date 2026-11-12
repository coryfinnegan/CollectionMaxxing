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
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        source.SetProfile(exif);
        source.SetProfile(new XmpProfile("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><note>secret-xmp</note></x:xmpmeta>"u8.ToArray()));
        source.SetProfile(ColorProfiles.SRGB);
        source.SetAttribute("comment", "secret-comment");
        var bytes = Encode(source, MagickFormat.Jpeg);
        using (var check = new MagickImage(bytes))
        {
            // the fixture really carries all four
            check.GetExifProfile().ShouldNotBeNull();
            check.GetXmpProfile().ShouldNotBeNull();
            check.GetColorProfile().ShouldNotBeNull();
            check.GetAttribute("comment").ShouldBe("secret-comment");
        }

        var still = _maker.Make(bytes);

        still.ShouldNotBeNull();
        IsJpeg(still).ShouldBeTrue();
        using var result = new MagickImage(still);
        result.Width.ShouldBe(2048u);
        result.Height.ShouldBe(1024u);
        result.GetExifProfile().ShouldBeNull();
        result.GetXmpProfile().ShouldBeNull();
        result.GetColorProfile().ShouldBeNull();
        result.GetAttribute("comment").ShouldBeNull();
        System.Text.Encoding.Latin1.GetString(still).ShouldNotContain("secret");
    }

    [Fact]
    public void An_svg_is_refused_even_though_the_decoder_could_read_it()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\"><rect width=\"40\" height=\"40\" fill=\"red\"/></svg>"u8.ToArray();

        _maker.Make(svg).ShouldBeNull();
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

    /// <summary>
    /// No HEIC fixture may be downloaded and this Magick build cannot write HEIC, but it writes AVIF, which sits in the same container: the file's brand
    /// (bytes 8 to 11 of its first box) is overwritten with "heic", so the header reads as HEIC and the allow-list entry for HEIC is what is under test.
    /// </summary>
    [Fact]
    public void A_picture_whose_header_says_heic_is_turned_into_a_jpeg()
    {
        using var source = Drawn(64, 48);
        var avif = Encode(source, MagickFormat.Avif);
        avif[4..8].ShouldBe("ftyp"u8.ToArray());
        "heic"u8.ToArray().CopyTo(avif, 8);

        var still = _maker.Make(avif);

        still.ShouldNotBeNull();
        IsJpeg(still).ShouldBeTrue();
    }

    [Fact]
    public void A_bad_setting_refuses_the_picture_instead_of_throwing()
    {
        var maker = new ShelfStillMaker(new ThrowingOptions());
        using var source = Drawn(32, 32);

        maker.Make(Encode(source, MagickFormat.Png)).ShouldBeNull();
    }

    private sealed class ThrowingOptions : Microsoft.Extensions.Options.IOptions<ShelfReaderOptions>
    {
        public ShelfReaderOptions Value => throw new Microsoft.Extensions.Options.OptionsValidationException("ShelfReader", typeof(ShelfReaderOptions), ["MaxLongEdge must be above 0."]);
    }
}
