using System.Diagnostics;
using CollectionMaxxing.Application.Scanning.Artwork;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using OpenCvSharp.Features2D;
using OpenCvSharp.Flann;

namespace CollectionMaxxing.Infrastructure.Scanning;

/// <summary>
/// Compares a cover capture against a candidate cover with SIFT feature matching (#438 stage 2): SIFT keypoints and
/// descriptors, a FLANN matcher with Lowe's ratio test, and RANSAC homography verification whose inlier count is the
/// sameness score. The capture is warped flat first when a quad is supplied; both images are scaled to
/// <see cref="CoverVerifyOptions.MaxDimension"/> so a phone photo cannot make this slow. CPU-bound, so it runs off the
/// request thread. Stateless.
/// </summary>
public sealed class SiftCoverVerifier(IOptionsMonitor<ScoringOptions> scoring) : ICoverVerifier
{
    public async Task<CoverComparison> CompareAsync(ReadOnlyMemory<byte> capture, ReadOnlyMemory<byte> cover, CoverQuad? quad, CancellationToken ct)
    {
        var options = scoring.CurrentValue.CoverVerify;
        var captureBytes = capture.ToArray();
        var coverBytes = cover.ToArray();
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => CompareCore(captureBytes, coverBytes, quad, options, ct), ct);
    }

    private static CoverComparison CompareCore(
        byte[] capture, byte[] cover, CoverQuad? quad, CoverVerifyOptions options, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var captureMat = Decode(capture, nameof(capture));
        using var coverMat = Decode(cover, nameof(cover));
        using var flat = quad is null ? ScaleDown(captureMat, options.MaxDimension) : WarpFlat(captureMat, quad, options.MaxDimension);
        using var target = ScaleDown(coverMat, options.MaxDimension);
        ct.ThrowIfCancellationRequested();

        using var sift = SIFT.Create(0, 3, 0.04, 10, 1.6);
        using var noMask = new Mat();
        using var captureDescriptors = new Mat();
        using var coverDescriptors = new Mat();
        sift.DetectAndCompute(flat, noMask, out var captureKeys, captureDescriptors, false);
        sift.DetectAndCompute(target, noMask, out var coverKeys, coverDescriptors, false);
        if (captureDescriptors.Empty() || coverDescriptors.Empty())
        {
            return new CoverComparison(0, captureKeys.Length, coverKeys.Length, Elapsed(started));
        }

        using var matcher = new FlannBasedMatcher(new KDTreeIndexParams(5), new SearchParams(50, 0, true));
        using var matchMask = new Mat();
        var good = matcher.KnnMatch(captureDescriptors, coverDescriptors, 2, matchMask, true)
            .Where(pair => pair.Length == 2 && pair[0].Distance < options.LoweRatio * pair[1].Distance)
            .Select(pair => pair[0])
            .ToList();
        if (good.Count < MinHomographyPoints)
        {
            return new CoverComparison(0, captureKeys.Length, coverKeys.Length, Elapsed(started));
        }

        var from = good.Select(m => captureKeys[m.QueryIdx].Pt).Select(AsPoint).ToList();
        var to = good.Select(m => coverKeys[m.TrainIdx].Pt).Select(AsPoint).ToList();
        using var inliers = new Mat();
        using var homography = Cv2.FindHomography(from, to, HomographyMethods.Ransac, options.RansacReprojThreshold, inliers, 2000, 0.995);
        var count = homography.Empty() || inliers.Empty() ? 0 : Cv2.CountNonZero(inliers);
        return new CoverComparison(count, captureKeys.Length, coverKeys.Length, Elapsed(started));
    }

    /// <summary>A homography needs four point pairs; fewer good matches cannot verify anything.</summary>
    private const int MinHomographyPoints = 4;

    private static Point2d AsPoint(Point2f point) => new(point.X, point.Y);

    private static Mat Decode(byte[] bytes, string what)
    {
        var mat = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidOperationException($"The {what} image could not be decoded.");
        }

        return mat;
    }

    /// <summary>The image at most <paramref name="maxDimension"/> on its longest side. Always a new matrix.</summary>
    private static Mat ScaleDown(Mat image, int maxDimension)
    {
        var longest = Math.Max(image.Cols, image.Rows);
        if (longest <= maxDimension)
        {
            return image.Clone();
        }

        var scale = (double)maxDimension / longest;
        var size = new Size(Math.Max(1, (int)(image.Cols * scale)), Math.Max(1, (int)(image.Rows * scale)));
        var scaled = new Mat();
        Cv2.Resize(image, scaled, size, 0, 0, InterpolationFlags.Linear);
        return scaled;
    }

    /// <summary>
    /// The quad warped flat to its own aspect ratio, at most <paramref name="maxDimension"/> on its longest side.
    /// The corners run clockwise from the top left, normalized to the capture.
    /// </summary>
    private static Mat WarpFlat(Mat capture, CoverQuad quad, int maxDimension)
    {
        var corners = new[]
        {
            new Point2f((float)(quad.TopLeft.X * capture.Cols), (float)(quad.TopLeft.Y * capture.Rows)),
            new Point2f((float)(quad.TopRight.X * capture.Cols), (float)(quad.TopRight.Y * capture.Rows)),
            new Point2f((float)(quad.BottomRight.X * capture.Cols), (float)(quad.BottomRight.Y * capture.Rows)),
            new Point2f((float)(quad.BottomLeft.X * capture.Cols), (float)(quad.BottomLeft.Y * capture.Rows)),
        };
        var width = Math.Max(Distance(corners[0], corners[1]), Distance(corners[3], corners[2]));
        var height = Math.Max(Distance(corners[0], corners[3]), Distance(corners[1], corners[2]));
        var scale = Math.Min(1.0, maxDimension / Math.Max(Math.Max(width, height), 1.0));
        var size = new Size(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
        var flat = new[]
        {
            new Point2f(0, 0),
            new Point2f(size.Width, 0),
            new Point2f(size.Width, size.Height),
            new Point2f(0, size.Height),
        };
        using var transform = Cv2.GetPerspectiveTransform(corners, flat);
        var warped = new Mat();
        Cv2.WarpPerspective(capture, warped, transform, size, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        return warped;
    }

    private static double Distance(Point2f a, Point2f b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
