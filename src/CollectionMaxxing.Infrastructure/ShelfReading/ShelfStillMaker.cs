using CollectionMaxxing.Application.ShelfReading;
using ImageMagick;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.ShelfReading;

/// <summary>
/// Makes the picture the reader is sent: an upright JPEG of at most <see cref="ShelfReaderOptions.MaxLongEdge"/> on its long
/// edge, with no metadata, from a HEIC or JPEG (Magick.NET reads HEIC through its bundled libheif). A smaller picture is not
/// enlarged. <c>JpegSanitizer</c> cannot do this: it does not decode and it drops the EXIF orientation. Used by the eval path.
/// </summary>
public sealed class ShelfStillMaker(IOptions<ShelfReaderOptions> options) : IShelfStillMaker
{
    private const int JpegQuality = 90;

    public byte[]? Make(byte[] picture)
    {
        if (picture is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var image = new MagickImage(picture);
            image.AutoOrient(); // turn the pixels upright, then the flag means nothing
            var longEdge = (uint)Math.Max(1, options.Value.MaxLongEdge);
            if (Math.Max(image.Width, image.Height) > longEdge)
            {
                image.Resize(new MagickGeometry(longEdge, longEdge) { IgnoreAspectRatio = false });
            }

            image.Strip(); // no EXIF, no ICC, no XMP: nothing about the owner's camera or place goes further
            if (image.HasAlpha)
            {
                image.ColorAlpha(MagickColors.White); // a JPEG has no transparency
            }

            image.Format = MagickFormat.Jpeg;
            image.Quality = JpegQuality;
            return image.ToByteArray();
        }
        catch (MagickException)
        {
            return null; // not a picture (or one we cannot decode)
        }
    }
}
