using System.Buffers.Binary;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Scanning.Photo;

/// <summary>A JPEG that passed <see cref="JpegSanitizer"/>: the bytes to store, and the size its frame header declares.</summary>
public sealed record CleanJpeg(byte[] Bytes, int Width, int Height);

/// <summary>
/// Checks that bytes are a sound JPEG and rewrites them without the metadata a phone puts in (docs/08: photos carry GPS). There is no image
/// library in this project, so it works on the file's segments and never decodes a pixel: it keeps the segments a decoder needs (quantisation
/// and Huffman tables, the frame header, restart interval, scan headers and the compressed scan data, byte for byte) and drops everything
/// else: EXIF/XMP/ICC and every other APPn segment, comments, anything after the end marker. Two harmless APP segments stay because
/// decoders read them: a JFIF header without a thumbnail, and Adobe's colour-transform flag. The result is the same picture without the
/// private parts. Pure and fast; it never throws for bad input, it answers with an error.
/// </summary>
public static class JpegSanitizer
{
    /// <summary>The largest upload accepted, in bytes.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>The largest width or height accepted, in pixels. The app sends an already-resized crop.</summary>
    public const int MaxDimension = 4096;

    private const byte Prefix = 0xFF;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;
    private const string Field = "file";

    public static DomainError NotJpeg { get; } = ScanningErrors.Invalid(
        "scan.photo_not_jpeg", "That file isn't a JPEG picture. Send the crop as a JPEG.", Field);

    public static DomainError Damaged { get; } = ScanningErrors.Invalid(
        "scan.photo_damaged", "That JPEG is damaged or cut short, so it can't be used. Take the picture again.", Field);

    public static DomainError TooLarge { get; } = ScanningErrors.Invalid(
        "scan.photo_too_large", $"That picture is too big. Send a crop of at most {MaxDimension} by {MaxDimension} pixels.", Field);

    /// <summary>Validates <paramref name="source"/> and returns it rewritten without metadata, or why it was refused.</summary>
    public static Result<CleanJpeg> Clean(ReadOnlySpan<byte> source)
    {
        if (source.Length < 3 || source[0] != Prefix || source[1] != StartOfImage || source[2] != Prefix)
        {
            return NotJpeg;
        }

        var output = new byte[source.Length]; // never larger than the input: it only drops things
        var written = 0;
        output[written++] = Prefix;
        output[written++] = StartOfImage;

        var position = 2;
        var width = 0;
        var height = 0;
        var sawFrame = false;
        var keptJfif = false;
        var keptAdobe = false;
        var sawScan = false;

        while (true)
        {
            // A marker is 0xFF and a code; any number of 0xFF fill bytes may come first.
            if (position >= source.Length || source[position] != Prefix)
            {
                return Damaged;
            }

            while (position < source.Length && source[position] == Prefix)
            {
                position++;
            }

            if (position >= source.Length)
            {
                return Damaged;
            }

            var marker = source[position++];
            if (marker == EndOfImage)
            {
                if (!sawFrame || !sawScan)
                {
                    return Damaged;
                }

                output[written++] = Prefix;
                output[written++] = EndOfImage;
                return new CleanJpeg(output.AsSpan(0, written).ToArray(), width, height);
            }

            // Nothing but segments with a length may stand between the start and the end (restart markers belong inside a scan).
            if (marker == 0x00 || marker == StartOfImage || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                return Damaged;
            }

            if (position + 2 > source.Length)
            {
                return Damaged;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(position, 2));
            if (length < 2 || position + length > source.Length)
            {
                return Damaged;
            }

            var payload = source.Slice(position + 2, length - 2);
            position += length;

            if (IsFrameHeader(marker))
            {
                if (sawFrame || payload.Length < 6)
                {
                    return Damaged;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2));
                if (width == 0 || height == 0)
                {
                    return Damaged;
                }

                if (width > MaxDimension || height > MaxDimension)
                {
                    return TooLarge;
                }

                sawFrame = true;
            }

            if (marker == StartOfScan && !sawFrame)
            {
                return Damaged;
            }

            if (Keep(marker, payload, ref keptJfif, ref keptAdobe))
            {
                output[written++] = Prefix;
                output[written++] = marker;
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(written, 2), length);
                written += 2;
                payload.CopyTo(output.AsSpan(written));
                written += payload.Length;
            }

            if (marker != StartOfScan)
            {
                continue;
            }

            // The compressed scan data runs to the next marker. Inside it 0xFF is followed by 0x00 (a data byte) or a restart marker.
            var start = position;
            while (true)
            {
                if (position >= source.Length)
                {
                    return Damaged;
                }

                if (source[position] != Prefix)
                {
                    position++;
                    continue;
                }

                if (position + 1 >= source.Length)
                {
                    return Damaged;
                }

                var next = source[position + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
                {
                    position += 2;
                    continue;
                }

                break; // a real marker (or fill before one): the segment loop reads it
            }

            source[start..position].CopyTo(output.AsSpan(written));
            written += position - start;
            sawScan = true;
        }
    }

    /// <summary>The frame headers (SOF0 to SOF15 except the table markers DHT, JPG and DAC).</summary>
    private static bool IsFrameHeader(byte marker) => marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);

    /// <summary>
    /// Whether the segment is one a decoder needs, or one of the two harmless APP segments. At most one of each of those is kept, and only
    /// when its fields are what the format allows, so nothing the uploader chose rides along.
    /// </summary>
    private static bool Keep(byte marker, ReadOnlySpan<byte> payload, ref bool keptJfif, ref bool keptAdobe)
    {
        switch (marker)
        {
            case 0xE0:
                // JFIF without a thumbnail: "JFIF\0", version 1.x, density unit 0 to 2, any density, thumbnail width and height 0.
                if (keptJfif || payload.Length != 14 || !payload.StartsWith("JFIF\0"u8) || payload[5] != 1 || payload[7] > 2 || payload[12] != 0 || payload[13] != 0)
                {
                    return false;
                }

                keptJfif = true;
                return true;
            case 0xEE:
                // The colour-transform flag some CMYK/RGB files need: "Adobe", version, two flag words, transform 0 to 2.
                if (keptAdobe || payload.Length != 12 || !payload.StartsWith("Adobe"u8) || payload[11] > 2)
                {
                    return false;
                }

                keptAdobe = true;
                return true;
            case 0xDB or 0xC4 or 0xCC or 0xDD or 0xDC or StartOfScan: // DQT, DHT, DAC, DRI, DNL, SOS
                return true;
            default:
                return IsFrameHeader(marker);
        }
    }
}
