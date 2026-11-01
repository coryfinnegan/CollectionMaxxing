namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// Where a read sits in the picture: x, y, width and height, each from 0 to 1, the origin at the top left (the phone's
/// <c>Rect</c>). It is how the server tells one spine read twice from two spines that read alike.
/// </summary>
public readonly record struct ReadBox(double X, double Y, double Width, double Height)
{
    /// <summary>The share of the smaller of two boxes the other must cover for the two to be one place.</summary>
    public const double SamePlaceShare = 0.5;

    /// <summary>How far outside 0 to 1 a value (or the end of a box) may be and still be moved into range, not dropped (floating point, and a box cut at the edge).</summary>
    public const double EdgeTolerance = 0.01;

    private const double Epsilon = 1e-9;

    /// <summary>
    /// Whether the two boxes are one place: the part they share is at least <see cref="SamePlaceShare"/> of the smaller box.
    /// Boxes that only touch share nothing. A small box inside a big one is the same place; the smaller box is what counts.
    /// </summary>
    public bool SamePlaceAs(ReadBox other)
    {
        var (shared, smaller) = Shared(other);
        return shared > 0 && shared >= (SamePlaceShare * smaller) - Epsilon;
    }

    /// <summary>
    /// The share of the smaller of the two boxes that the two share, 0 to 1; 0 when they only touch or miss. It tells which of
    /// several boxes that are all one place is the most one place, the way the phone picks the item a read sits on.
    /// </summary>
    public double OverlapShare(ReadBox other)
    {
        var (shared, smaller) = Shared(other);
        return shared > 0 && smaller > 0 ? shared / smaller : 0;
    }

    /// <summary>The area the two boxes share (0 when they only touch or miss) and the area of the smaller of them.</summary>
    private (double Shared, double Smaller) Shared(ReadBox other)
    {
        var sharedWidth = Math.Min(X + Width, other.X + other.Width) - Math.Max(X, other.X);
        var sharedHeight = Math.Min(Y + Height, other.Y + other.Height) - Math.Max(Y, other.Y);
        var smaller = Math.Min(Width * Height, other.Width * other.Height);
        return sharedWidth <= 0 || sharedHeight <= 0 ? (0, smaller) : (sharedWidth * sharedHeight, smaller);
    }

    /// <summary>
    /// Whether two reads are in the same place. A read with no box (an older phone, or a read the phone gave no position) could be
    /// anywhere, so it is never held apart from another read; two boxes are compared with <see cref="SamePlaceAs"/>.
    /// </summary>
    public static bool SamePlace(ReadBox? first, ReadBox? second) =>
        first is not { } a || second is not { } b || a.SamePlaceAs(b);
}

/// <summary>
/// A box as the phone sent it, every member a claim to check. A box is a hint, so a bad one is never a reason to refuse a read:
/// <see cref="Clamped"/> is the box with any value within <see cref="ReadBox.EdgeTolerance"/> of the 0 to 1 range moved into it
/// (and the box trimmed back to the picture's edge), and null, meaning the read has no box, when a member is missing, not finite
/// or further out of range than that, the width or height is not above 0, or the box runs further past the edge than that.
/// </summary>
public sealed record ReadBoxInput(double? X, double? Y, double? Width, double? Height)
{
    public ReadBox? Clamped()
    {
        if (Fit(X) is not { } x || Fit(Y) is not { } y || Fit(Width) is not { } width || Fit(Height) is not { } height)
        {
            return null;
        }

        if (x + width > 1 + ReadBox.EdgeTolerance || y + height > 1 + ReadBox.EdgeTolerance)
        {
            return null;
        }

        width = Math.Min(width, 1 - x);
        height = Math.Min(height, 1 - y);
        return width > 0 && height > 0 ? new ReadBox(x, y, width, height) : null;
    }

    /// <summary>The value in range: as it is, or moved to 0 or 1 when it is only just outside. Null when missing, not finite or further out.</summary>
    private static double? Fit(double? value) =>
        value is { } v && double.IsFinite(v) && v >= -ReadBox.EdgeTolerance && v <= 1 + ReadBox.EdgeTolerance ? Math.Clamp(v, 0, 1) : null;
}
