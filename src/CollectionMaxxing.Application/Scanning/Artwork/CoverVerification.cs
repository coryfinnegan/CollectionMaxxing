using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Scanning.Artwork;

/// <summary>One normalized corner of a cover quad, origin top-left, each value 0–1.</summary>
public sealed record CoverPoint(double X, double Y);

/// <summary>The cover's four corners in the capture, clockwise from the top left, as the phone detected them.</summary>
public sealed record CoverQuad(CoverPoint TopLeft, CoverPoint TopRight, CoverPoint BottomRight, CoverPoint BottomLeft)
{
    /// <summary>A quad flatter than this is a line, not a cover.</summary>
    public const double MinArea = 1e-6;

    /// <summary>Null when the quad is usable: four finite points inside the picture forming a convex quad.</summary>
    public static DomainError? Validate(CoverQuad? quad)
    {
        if (quad is null)
        {
            return null;
        }

        var corners = new[] { quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft };
        if (corners.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1))
        {
            return ScanningErrors.Invalid(
                "scan.cover_quad_invalid", "The cover corners must be four points inside the picture, each from 0 to 1.", "quad");
        }

        if (Area(corners) <= MinArea || !IsConvex(corners))
        {
            return ScanningErrors.Invalid(
                "scan.cover_quad_invalid", "The cover corners must form a convex quad with a real area.", "quad");
        }

        return null;
    }

    /// <summary>The polygon area (shoelace), in normalized square units.</summary>
    private static double Area(CoverPoint[] corners)
    {
        var sum = 0.0;
        for (var i = 0; i < corners.Length; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Length];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(sum) / 2;
    }

    /// <summary>Whether the corners turn the same way all the way round (no bowtie, no dent).</summary>
    private static bool IsConvex(CoverPoint[] corners)
    {
        var sign = 0.0;
        for (var i = 0; i < corners.Length; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Length];
            var c = corners[(i + 2) % corners.Length];
            var cross = ((b.X - a.X) * (c.Y - b.Y)) - ((b.Y - a.Y) * (c.X - b.X));
            if (cross != 0)
            {
                if (sign != 0 && Math.Sign(cross) != Math.Sign(sign))
                {
                    return false;
                }

                sign = cross;
            }
        }

        return true;
    }
}

/// <summary>What one capture-to-cover comparison found: the RANSAC inlier count, the keypoints behind it, and how long it took.</summary>
public sealed record CoverComparison(int Inliers, int CaptureKeypoints, int CoverKeypoints, long Ms);

/// <summary>
/// Compares a cover capture against one candidate cover image. Implemented by Infrastructure over OpenCvSharp (SIFT +
/// FLANN + Lowe ratio + RANSAC homography); the capture is warped flat first when a quad is supplied. Pure bytes in and
/// out: no imaging types cross this port. Throws when either image cannot be decoded (the caller records it per cover);
/// only the caller's own cancellation escapes otherwise.
/// </summary>
public interface ICoverVerifier
{
    Task<CoverComparison> CompareAsync(ReadOnlyMemory<byte> capture, ReadOnlyMemory<byte> cover, CoverQuad? quad, CancellationToken ct);
}

/// <summary>How one alternative's cover compared: the inlier count, the visual and combined scores, or why it was not compared.</summary>
public sealed record CoverScore(Guid ReleaseId, int? Inliers, double? Visual, double? Combined, string? Failure);

/// <summary>The preselected pick Review shows checked: the release, and the stable reason code behind it.</summary>
public sealed record StoredCoverPick(Guid ReleaseId, string Reason);

/// <summary>Errors of the cover verification that the endpoint maps to their own status.</summary>
public static class CoverVerifyErrors
{
    public static readonly DomainError NotNeeded = new(
        ErrorKind.Conflict, "scan.cover_not_needed", "This item is not a close call, so there is no cover to verify.");

    public static readonly DomainError Changed = new(
        ErrorKind.Conflict, "scan.cover_changed", "This item was answered again while its covers were compared. Look at the new answer first.");

    public static readonly DomainError NoCovers = new(
        ErrorKind.Conflict, "scan.cover_no_covers", "None of this item's alternatives has a cover to compare.");

    public static readonly DomainError Unavailable = new(
        ErrorKind.Conflict, "scan.cover_unavailable", "No cover could be compared right now. Try again later.");
}

/// <summary>
/// The stage-2 rules (#438): inliers to a visual score, the visual score blended with the text score, and when the top
/// cover is preselected for Review. Pure, so the rules are one place and easy to test.
/// </summary>
public static class CoverVerification
{
    /// <summary>The trace step that marks a candidate as cover-verified.</summary>
    public const string StepName = "cover_verify";

    /// <summary>The reason a preselected pick carries: its cover matched the capture.</summary>
    public const string PreselectReason = "cover_match";

    /// <summary>Why an alternative went uncompared: its cover could not be read. Stable string: it is recorded in the trace.</summary>
    public const string CompareFailed = "compare-failed";

    /// <summary>Why an alternative went uncompared: it has no cover to compare. Stable string: it is recorded in the trace.</summary>
    public const string NoCover = "no-cover";

    private const int Decimals = 9;

    /// <summary>One compared cover, best first. Ties keep their input order.</summary>
    public sealed record RankedCover(Guid ReleaseId, double TextScore, int Inliers, double Visual, double Combined);

    /// <summary>
    /// Inliers to a 0–1 visual score: at or below <see cref="CoverVerifyOptions.WeakInliers"/> is no match, at or above
    /// <see cref="CoverVerifyOptions.StrongInliers"/> a clear one, linear between.
    /// </summary>
    public static double VisualScore(int inliers, CoverVerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (inliers <= options.WeakInliers)
        {
            return 0.0;
        }

        if (inliers >= options.StrongInliers)
        {
            return 1.0;
        }

        return Math.Round((double)(inliers - options.WeakInliers) / (options.StrongInliers - options.WeakInliers), Decimals);
    }

    /// <summary>The text score blended with the visual score; the visual carries <see cref="CoverVerifyOptions.VisualWeight"/>.</summary>
    public static double CombinedScore(double textScore, double visualScore, CoverVerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var weight = Math.Clamp(options.VisualWeight, 0.0, 1.0);
        return Math.Round(Math.Clamp((textScore * (1 - weight)) + (visualScore * weight), 0.0, 1.0), Decimals);
    }

    /// <summary>
    /// Ranks the compared covers best first and decides the preselect: the top cover is picked when it is a clear visual
    /// match and leads the runner-up (none counts as unlimited) by at least <see cref="CoverVerifyOptions.PreselectMargin"/>.
    /// A relative best that matches nothing is never picked.
    /// </summary>
    public static (IReadOnlyList<RankedCover> Ranked, Guid? Preselected) Rank(
        IReadOnlyList<(Guid ReleaseId, double TextScore, int Inliers)> compared, CoverVerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(compared);
        ArgumentNullException.ThrowIfNull(options);

        var ranked = compared
            .Select(c =>
            {
                var visual = VisualScore(c.Inliers, options);
                return new RankedCover(c.ReleaseId, c.TextScore, c.Inliers, visual, CombinedScore(c.TextScore, visual, options));
            })
            .OrderByDescending(c => c.Combined)
            .ToList();

        Guid? preselected = null;
        if (ranked.Count > 0 && ranked[0].Visual >= 1.0)
        {
            var lead = ranked.Count > 1
                ? Math.Round(ranked[0].Combined - ranked[1].Combined, Decimals)
                : double.PositiveInfinity;
            if (lead >= Math.Round(options.PreselectMargin, Decimals))
            {
                preselected = ranked[0].ReleaseId;
            }
        }

        return (ranked, preselected);
    }
}
