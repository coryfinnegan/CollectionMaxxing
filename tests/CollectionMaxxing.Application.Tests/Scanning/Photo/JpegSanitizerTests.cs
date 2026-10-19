using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Scanning.Photo;

public class JpegSanitizerTests
{
    private static byte[] Clean(byte[] source)
    {
        var result = JpegSanitizer.Clean(source);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? string.Empty : result.Error!.Message);
        return result.Value.Bytes;
    }

    private static string? ErrorCode(byte[] source)
    {
        var result = JpegSanitizer.Clean(source);
        result.IsSuccess.ShouldBeFalse();
        return result.Error!.Code;
    }

    [Fact]
    public void A_plain_picture_comes_back_byte_for_byte()
    {
        Clean(JpegFixtures.Plain()).ShouldBe(JpegFixtures.Plain());
    }

    [Fact]
    public void The_size_comes_from_the_frame_header()
    {
        var result = JpegSanitizer.Clean(JpegFixtures.WithSize(640, 480));

        result.IsSuccess.ShouldBeTrue();
        (result.Value.Width, result.Value.Height).ShouldBe((640, 480));
    }

    [Fact]
    public void Exif_gps_icc_photoshop_comment_and_trailing_bytes_are_all_removed_and_the_picture_is_untouched()
    {
        var dirty = JpegFixtures.WithPhoneMetadata();
        JpegFixtures.Contains(dirty, JpegFixtures.GpsText).ShouldBeTrue(); // the fixture really carries it

        var clean = Clean(dirty);

        foreach (var secret in new[] { JpegFixtures.GpsText, "Exif", "GPS", JpegFixtures.IccText, JpegFixtures.PhotoshopText, JpegFixtures.CommentText, JpegFixtures.TrailingText })
        {
            JpegFixtures.Contains(clean, secret).ShouldBeFalse(secret);
        }

        clean.ShouldBe(JpegFixtures.Plain()); // every byte of the picture itself survives
    }

    [Fact]
    public void Cleaning_a_cleaned_picture_changes_nothing()
    {
        var once = Clean(JpegFixtures.WithPhoneMetadata());

        Clean(once).ShouldBe(once);
    }

    [Fact]
    public void A_jfif_header_with_an_embedded_thumbnail_is_removed_but_the_adobe_colour_flag_stays()
    {
        var plain = JpegFixtures.Plain();
        byte[] thumbnailJfif = [0xFF, 0xE0, 0x00, 0x13, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0xAA, 0xBB, 0xCC];
        byte[] adobe = [0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, 0, 0, 0, 0, 1];
        var input = new List<byte> { 0xFF, 0xD8 };
        input.AddRange(thumbnailJfif);
        input.AddRange(adobe);
        input.AddRange(plain.AsSpan(2 + 18)); // after SOI and the plain file's own 18-byte JFIF segment

        var clean = Clean([.. input]);

        JpegFixtures.Contains(clean, "JFIF").ShouldBeFalse();
        clean.AsSpan(2, adobe.Length).ToArray().ShouldBe(adobe);
    }

    private static byte[] Jfif(byte major = 1, byte units = 1, byte thumbWidth = 0, byte fill = 0) =>
        [0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, major, 1, units, fill, fill, fill, fill, thumbWidth, 0];

    private static byte[] AdobeSegment(byte transform = 1, byte fill = 0) =>
        [0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, fill, fill, fill, fill, transform];

    /// <summary>The plain picture with <paramref name="extra"/> segments placed straight after its own JFIF header.</summary>
    private static byte[] PlainWith(params byte[][] extra)
    {
        var plain = JpegFixtures.Plain();
        var input = new List<byte>(plain.AsSpan(0, 2 + 18).ToArray());
        foreach (var segment in extra)
        {
            input.AddRange(segment);
        }

        input.AddRange(plain.AsSpan(2 + 18));
        return [.. input];
    }

    [Fact]
    public void Stacked_jfif_and_adobe_segments_are_cut_down_to_one_of_each_so_nothing_big_rides_along()
    {
        var many = Enumerable.Repeat(Jfif(fill: 0x41), 500).Concat(Enumerable.Repeat(AdobeSegment(fill: 0x42), 500)).ToArray();
        var dirty = PlainWith(many);

        var clean = Clean(dirty);

        clean.Length.ShouldBe(JpegFixtures.Plain().Length + AdobeSegment().Length); // the plain file's own JFIF, plus the first Adobe only
        clean.Length.ShouldBeLessThan(JpegFixtures.Plain().Length + 100);
    }

    [Fact]
    public void A_second_jfif_header_is_removed_even_when_it_looks_fine()
    {
        Clean(PlainWith(Jfif())).ShouldBe(JpegFixtures.Plain());
    }

    [Theory]
    [InlineData(2, 1, 0)] // version 2
    [InlineData(1, 3, 0)] // density unit 3
    [InlineData(1, 1, 5)] // a thumbnail width
    public void A_jfif_header_with_fields_the_format_does_not_allow_is_removed(byte major, byte units, byte thumbWidth)
    {
        var plain = JpegFixtures.Plain();
        var input = new List<byte>(plain.AsSpan(0, 2).ToArray());
        input.AddRange(Jfif(major, units, thumbWidth));
        input.AddRange(plain.AsSpan(2 + 18));

        JpegFixtures.Contains(Clean([.. input]), "JFIF").ShouldBeFalse();
    }

    [Fact]
    public void An_adobe_segment_with_a_transform_the_format_does_not_define_is_removed()
    {
        JpegFixtures.Contains(Clean(PlainWith(AdobeSegment(transform: 9))), "Adobe").ShouldBeFalse();
        JpegFixtures.Contains(Clean(PlainWith(AdobeSegment(transform: 2))), "Adobe").ShouldBeTrue();
    }

    [Fact]
    public void Scan_data_with_stuffed_bytes_restart_markers_and_fill_is_copied_exactly()
    {
        var plain = JpegFixtures.Plain();
        var scanStart = plain.AsSpan().LastIndexOf(new byte[] { 0xFF, 0xDA });
        var headerEnd = scanStart + 2 + ((plain[scanStart + 2] << 8) | plain[scanStart + 3]);
        byte[] entropy = [0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0xFF, 0x00, 0xFF, 0xD1, 0x78];
        var input = plain.AsSpan(0, headerEnd).ToArray().Concat(entropy).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();

        Clean(input).ShouldBe(input);
    }

    [Theory]
    [InlineData(4096, 8)]
    [InlineData(8, 4096)]
    public void A_picture_at_the_size_limit_is_accepted(int width, int height)
    {
        JpegSanitizer.Clean(JpegFixtures.WithSize(width, height)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(4097, 8)]
    [InlineData(8, 4097)]
    [InlineData(65535, 65535)]
    public void A_picture_over_the_size_limit_is_refused(int width, int height)
    {
        ErrorCode(JpegFixtures.WithSize(width, height)).ShouldBe("scan.photo_too_large");
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(8, 0)]
    public void A_picture_with_no_size_is_refused(int width, int height)
    {
        ErrorCode(JpegFixtures.WithSize(width, height)).ShouldBe("scan.photo_damaged");
    }

    [Fact]
    public void A_png_an_html_page_and_nothing_at_all_are_not_jpegs()
    {
        ErrorCode(JpegFixtures.Png()).ShouldBe("scan.photo_not_jpeg");
        ErrorCode(JpegFixtures.Html()).ShouldBe("scan.photo_not_jpeg");
        ErrorCode([]).ShouldBe("scan.photo_not_jpeg");
        ErrorCode([0xFF]).ShouldBe("scan.photo_not_jpeg");
    }

    [Fact]
    public void A_picture_cut_off_anywhere_is_refused_as_damaged()
    {
        ErrorCode(JpegFixtures.TruncatedInScan()).ShouldBe("scan.photo_damaged");
        ErrorCode(JpegFixtures.TruncatedInHeader()).ShouldBe("scan.photo_damaged");

        var plain = JpegFixtures.Plain();
        for (var length = 3; length < plain.Length; length += 7)
        {
            JpegSanitizer.Clean(plain.AsSpan(0, length)).IsSuccess.ShouldBeFalse($"cut at {length}");
        }
    }

    [Fact]
    public void A_segment_that_claims_to_run_past_the_end_is_refused()
    {
        byte[] input = [0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 1, 2, 3];

        ErrorCode(input).ShouldBe("scan.photo_damaged");
    }

    [Fact]
    public void A_picture_with_no_frame_or_no_scan_is_refused()
    {
        byte[] onlyMarkers = [0xFF, 0xD8, 0xFF, 0xFE, 0x00, 0x04, 0x41, 0x42, 0xFF, 0xD9];

        ErrorCode(onlyMarkers).ShouldBe("scan.photo_damaged");
    }

    [Fact]
    public void A_picture_that_ends_right_after_a_complete_segment_is_damaged_not_a_crash()
    {
        // Start of image, then a comment segment of length 2 (no text), then nothing: no frame, no end marker.
        byte[] source = [0xFF, 0xD8, 0xFF, 0xFE, 0x00, 0x02];

        ErrorCode(source).ShouldBe(JpegSanitizer.Damaged.Code);
    }
}
