using OpenCvSharp;

namespace CollectionMaxxing.TestSupport;

/// <summary>
/// Synthetic Suddenly You covers (#438 stage 2 fixtures). Cory's retained capture is a phone photo, not bytes in the
/// repo, so the fixtures stand in: two editions sharing the title and author but with different cover art, a capture
/// simulated by warping one edition with perspective, blur and a lighting shift, and an unrelated third cover. Every
/// coordinate is fixed: the same bytes every run, no randomness, no downloads.
/// </summary>
public static class CoverFixtures
{
    public const int CoverWidth = 600;
    public const int CoverHeight = 900;
    public const int CaptureWidth = 900;
    public const int CaptureHeight = 1200;

    /// <summary>The "2001 edition": cream, navy frame, a sunburst behind the title.</summary>
    public static Mat Edition2001()
    {
        var cover = new Mat(new Size(CoverWidth, CoverHeight), MatType.CV_8UC3, new Scalar(220, 235, 240));
        Cv2.Rectangle(cover, new Rect(24, 24, CoverWidth - 48, CoverHeight - 48), new Scalar(40, 40, 120), 6);
        Cv2.Rectangle(cover, new Rect(44, 44, CoverWidth - 88, CoverHeight - 88), new Scalar(40, 40, 120), 2);
        Sunburst(cover, new Point(300, 330), 150, new Scalar(60, 90, 200));
        Cv2.PutText(cover, "SUDDENLY YOU", new Point(70, 300), HersheyFonts.HersheySimplex, 2.2, new Scalar(30, 30, 110), 4);
        Cv2.PutText(cover, "LISA KLEYPAS", new Point(120, 620), HersheyFonts.HersheySimplex, 1.6, new Scalar(30, 30, 110), 3);
        Cv2.Line(cover, new Point(120, 650), new Point(480, 650), new Scalar(30, 30, 110), 2);
        for (var i = 0; i < 5; i++)
        {
            Cv2.Circle(cover, new Point(110 + (i * 95), 730), 26, new Scalar(60, 120, 180), 3);
        }

        Cv2.Rectangle(cover, new Rect(44, 790, CoverWidth - 88, 66), new Scalar(40, 40, 120), -1);
        Cv2.PutText(cover, "AVON BOOKS", new Point(170, 834), HersheyFonts.HersheySimplex, 1.2, new Scalar(220, 235, 240), 2);
        return cover;
    }

    /// <summary>The "2003 edition": same title and author, different art — dusty rose, arches, stars.</summary>
    public static Mat Edition2003()
    {
        var cover = new Mat(new Size(CoverWidth, CoverHeight), MatType.CV_8UC3, new Scalar(190, 170, 210));
        Cv2.Rectangle(cover, new Rect(24, 24, CoverWidth - 48, CoverHeight - 48), new Scalar(40, 90, 50), 6);
        for (var i = 0; i < 3; i++)
        {
            Cv2.Ellipse(cover, new Point(300, 330), new Size(200 - (i * 45), 130 - (i * 30)), 0, 0, 360, new Scalar(50, 120, 70), 4);
        }

        Cv2.PutText(cover, "SUDDENLY YOU", new Point(60, 200), HersheyFonts.HersheyDuplex, 2.4, new Scalar(35, 70, 40), 3);
        Cv2.PutText(cover, "LISA KLEYPAS", new Point(170, 590), HersheyFonts.HersheyComplexSmall, 2.0, new Scalar(35, 70, 40), 2);
        Star(cover, new Point(150, 680), 34, new Scalar(50, 120, 70));
        Star(cover, new Point(300, 720), 44, new Scalar(50, 120, 70));
        Star(cover, new Point(450, 680), 34, new Scalar(50, 120, 70));
        Cv2.Rectangle(cover, new Rect(44, 790, CoverWidth - 88, 66), new Scalar(40, 90, 50), -1);
        Cv2.PutText(cover, "HARPER TORCH", new Point(150, 834), HersheyFonts.HersheySimplex, 1.2, new Scalar(190, 170, 210), 2);
        return cover;
    }

    /// <summary>An unrelated book, for the easy negative.</summary>
    public static Mat OtherBook()
    {
        var cover = new Mat(new Size(CoverWidth, CoverHeight), MatType.CV_8UC3, new Scalar(210, 205, 195));
        Cv2.Rectangle(cover, new Rect(24, 24, CoverWidth - 48, CoverHeight - 48), new Scalar(90, 70, 30), 6);
        for (var i = 0; i < 6; i++)
        {
            var y = 150 + (i * 90);
            Cv2.Line(cover, new Point(80, y), new Point(520, y + 40), new Scalar(110, 90, 50), 5);
        }

        Cv2.PutText(cover, "DUNE", new Point(200, 420), HersheyFonts.HersheySimplex, 3.0, new Scalar(80, 60, 25), 5);
        Cv2.PutText(cover, "FRANK HERBERT", new Point(130, 520), HersheyFonts.HersheySimplex, 1.3, new Scalar(80, 60, 25), 3);
        Cv2.Rectangle(cover, new Rect(200, 640, 200, 120), new Scalar(90, 70, 30), 4);
        return cover;
    }

    /// <summary>A solid cover with nothing to match: no keypoints, no inliers, no crash.</summary>
    public static Mat Blank() => new(new Size(CoverWidth, CoverHeight), MatType.CV_8UC3, new Scalar(128, 128, 128));

    /// <summary>The cover corners in a capture, normalized to the capture size.</summary>
    public sealed record CaptureCorners(Point2f TopLeft, Point2f TopRight, Point2f BottomRight, Point2f BottomLeft);

    /// <summary>A gentle tilt, like a photo taken slightly off-axis.</summary>
    public static CaptureCorners GentleCorners() => new(
        new Point2f(120, 80), new Point2f(760, 150), new Point2f(700, 1100), new Point2f(90, 1030));

    /// <summary>A hard tilt, like a photo grabbed at an angle: the warp has real work to do.</summary>
    public static CaptureCorners HardCorners() => new(
        new Point2f(30, 330), new Point2f(860, 60), new Point2f(590, 1140), new Point2f(250, 1080));

    /// <summary>
    /// A phone capture of the cover: warped into a dark photo at the given corners, then blurred and darkened a touch.
    /// Returns the JPEG bytes the port receives.
    /// </summary>
    public static byte[] CaptureOf(Mat cover, CaptureCorners corners)
    {
        var from = new[] { new Point2f(0, 0), new Point2f(cover.Cols, 0), new Point2f(cover.Cols, cover.Rows), new Point2f(0, cover.Rows) };
        var to = new[] { corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft };
        using var transform = Cv2.GetPerspectiveTransform(from, to);
        using var photo = new Mat(new Size(CaptureWidth, CaptureHeight), MatType.CV_8UC3, new Scalar(25, 22, 20));
        Cv2.WarpPerspective(cover, photo, transform, photo.Size(), InterpolationFlags.Linear, BorderTypes.Transparent);
        using var soft = new Mat();
        Cv2.GaussianBlur(photo, soft, new Size(3, 3), 0.8, 0.8, BorderTypes.Default);
        using var dimmed = new Mat();
        soft.ConvertTo(dimmed, MatType.CV_8U, 0.92, -8);
        Cv2.ImEncode(".jpg", dimmed, out var bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));
        return bytes;
    }

    /// <summary>The cover as the bytes a provider would serve (PNG: exactly the art, no photo noise).</summary>
    public static byte[] PngOf(Mat cover)
    {
        Cv2.ImEncode(".png", cover, out var bytes, Array.Empty<int>());
        return bytes;
    }

    private static void Sunburst(Mat cover, Point center, int radius, Scalar color)
    {
        for (var i = 0; i < 12; i++)
        {
            var angle = (Math.PI * i) / 6;
            var end = new Point(center.X + (int)(radius * Math.Cos(angle)), center.Y + (int)(radius * Math.Sin(angle)));
            Cv2.Line(cover, center, end, color, 3);
        }

        Cv2.Circle(cover, center, 40, color, -1);
    }

    private static void Star(Mat cover, Point center, int radius, Scalar color)
    {
        var points = new Point[10];
        for (var i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? radius : radius / 2;
            var angle = (Math.PI * i) / 5;
            points[i] = new Point(center.X + (int)(r * Math.Cos(angle)), center.Y + (int)(r * Math.Sin(angle)));
        }

        Cv2.Polylines(cover, [points], true, color, 3);
    }
}
