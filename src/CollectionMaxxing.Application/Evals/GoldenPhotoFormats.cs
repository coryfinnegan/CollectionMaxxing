using System.Buffers.Binary;

namespace CollectionMaxxing.Application.Evals;

/// <summary>The two kinds of picture the golden folder holds and an upload may add.</summary>
public enum GoldenPhotoFormat
{
    Jpeg,
    Heic,
}

/// <summary>
/// What a golden picture is, decided from its first bytes alone. The upload's content type, its name and its extension are never looked at:
/// a caller can claim anything, and the file written must be what the bytes say it is.
/// </summary>
public static class GoldenPhotoFormats
{
    /// <summary>The bytes that hold a <c>ftyp</c> box's size, name and major brand.</summary>
    private const int FtypHead = 12;

    /// <summary>The smallest a <c>ftyp</c> box can be: its 8-byte header, the 4-byte major brand and the 4-byte minor version.</summary>
    private const uint MinimumFtypBox = 16;

    /// <summary>
    /// JPEG starts <c>FF D8 FF</c>. HEIC is an ISO base media file: a <c>ftyp</c> box (size, "ftyp", major brand) whose major brand is
    /// <c>heic</c>, <c>heix</c>, <c>mif1</c> or <c>msf1</c>. Anything else, a movie (<c>mp42</c>) or an AVIF included, is null.
    /// </summary>
    public static GoldenPhotoFormat? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return GoldenPhotoFormat.Jpeg;
        }

        if (head.Length < FtypHead || !head.Slice(4, 4).SequenceEqual("ftyp"u8) || BinaryPrimitives.ReadUInt32BigEndian(head) < MinimumFtypBox)
        {
            return null;
        }

        var brand = head.Slice(8, 4);
        return brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8)
            ? GoldenPhotoFormat.Heic
            : null;
    }

    /// <summary>The extension the picture is saved under (<c>FileGoldenSet</c> lists exactly these, <c>.jpeg</c> too, when it scans).</summary>
    public static string Extension(GoldenPhotoFormat format) => format switch
    {
        GoldenPhotoFormat.Jpeg => ".jpg",
        GoldenPhotoFormat.Heic => ".heic",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

/// <summary>Names the PC's file system cannot hold even though <see cref="EvalIds.IsValid"/> allows them.</summary>
public static class GoldenPhotoNames
{
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True for a Windows device name (<c>con</c>, <c>nul</c>, <c>com1</c>…): <c>con.jpg</c> is the console there, not a file. Refused on every OS, so an id means the same everywhere.</summary>
    public static bool IsReserved(string id) => Devices.Contains(id);
}
