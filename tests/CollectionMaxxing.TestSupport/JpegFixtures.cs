using System.Text;

namespace CollectionMaxxing.TestSupport;

/// <summary>
/// Tiny JPEGs for the photo tests, built from one real 8x8 baseline JPEG (a generated test picture, not a photo) by inserting the
/// segments a phone writes: EXIF with GPS, an ICC profile, a comment, Photoshop data, and trailing bytes after the end marker.
/// </summary>
public static class JpegFixtures
{
    /// <summary>Text inside the fake EXIF segment. If it shows up in a stored file, the metadata was not stripped.</summary>
    public const string GpsText = "GPSLatitude 40.7128 N GPSLongitude 74.0060 W";

    public const string CommentText = "private-comment-text";

    public const string TrailingText = "TRAILING-SECRET-AFTER-EOI";

    public const string IccText = "ICC_PROFILE-bytes";

    public const string PhotoshopText = "Photoshop 3.0 8BIM caption";

    // 8x8, baseline, JFIF APP0 (no thumbnail), two quantisation tables, one frame, four Huffman tables, one scan.
    private const string Base64 =
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxl"
        + "Z2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCAAIAAgDASIAAhEB"
        + "AxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS"
        + "0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKz"
        + "tLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgEC"
        + "BAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpj"
        + "ZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6"
        + "/9oADAMBAAIRAxEAPwCrYaJ0+WiiisauIqc25eBxVX2K1P/Z";

    /// <summary>A small, valid, metadata-free baseline JPEG (with only the JFIF header).</summary>
    public static byte[] Plain() => Convert.FromBase64String(Base64);

    /// <summary>The same picture with EXIF (GPS), an ICC profile, Photoshop data and a comment, and bytes after the end marker.</summary>
    public static byte[] WithPhoneMetadata()
    {
        var plain = Plain();
        var afterSoi = plain.AsSpan(2).ToArray();
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        WriteSegment(output, 0xE1, Encoding.ASCII.GetBytes("Exif\0\0II*\0" + GpsText));
        WriteSegment(output, 0xE2, Encoding.ASCII.GetBytes(IccText));
        WriteSegment(output, 0xED, Encoding.ASCII.GetBytes(PhotoshopText));
        WriteSegment(output, 0xFE, Encoding.ASCII.GetBytes(CommentText));
        output.Write(afterSoi);
        output.Write(Encoding.ASCII.GetBytes(TrailingText));
        return output.ToArray();
    }

    /// <summary>The picture cut off in the middle of its scan, so it has no end marker.</summary>
    public static byte[] TruncatedInScan() => Plain().AsSpan(0, Plain().Length - 12).ToArray();

    /// <summary>The picture cut off inside a header segment.</summary>
    public static byte[] TruncatedInHeader() => Plain().AsSpan(0, 120).ToArray();

    /// <summary>The picture with the frame header claiming a different size.</summary>
    public static byte[] WithSize(int width, int height)
    {
        var bytes = Plain();
        for (var i = 2; i < bytes.Length - 9; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xC0)
            {
                bytes[i + 5] = (byte)(height >> 8);
                bytes[i + 6] = (byte)height;
                bytes[i + 7] = (byte)(width >> 8);
                bytes[i + 8] = (byte)width;
                return bytes;
            }
        }

        throw new InvalidOperationException("The fixture has no baseline frame header.");
    }

    /// <summary>A PNG signature and a little more: what a PNG renamed ".jpg" looks like.</summary>
    public static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1];

    public static byte[] Html() => Encoding.UTF8.GetBytes("<html><body><script>alert(1)</script></body></html>");

    /// <summary>A "JPEG" over the size limit: it starts like one, then is padded.</summary>
    public static byte[] Oversize(int bytes)
    {
        var padded = new byte[bytes];
        Plain().CopyTo(padded, 0);
        return padded;
    }

    /// <summary>True when <paramref name="haystack"/> holds <paramref name="text"/> as ASCII.</summary>
    public static bool Contains(ReadOnlySpan<byte> haystack, string text) => haystack.IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    private static void WriteSegment(Stream output, byte marker, byte[] payload)
    {
        output.WriteByte(0xFF);
        output.WriteByte(marker);
        var length = payload.Length + 2;
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)length);
        output.Write(payload);
    }
}
