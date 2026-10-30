namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// Where a read sits in the picture: x, y, width and height, each from 0 to 1, the origin at the top left (the phone's
/// <c>Rect</c>). It is how the server tells one spine read twice from two spines that read alike.
/// </summary>
public readonly record struct ReadBox(double X, double Y, double Width, double Height)
{
    /// <summary>The share of the smaller of two boxes the other must cover for the two to be one place.</summary>
    public const double SamePlaceShare = 0.5;

    /// <summary>How far past the picture's edge a box may reach (floating point, and a box the camera cut at the edge).</summary>
    public const double EdgeTolerance = 0.01;

    private const double Epsilon = 1e-9;

    /// <summary>
    /// Whether the two boxes are one place: the part they share is at least <see cref="SamePlaceShare"/> of the smaller box.
    /// Boxes that only touch share nothing. A small box inside a big one is the same place; the smaller box is what counts.
    /// </summary>
    public bool SamePlaceAs(ReadBox other)
    {
        var sharedWidth = Math.Min(X + Width, other.X + other.Width) - Math.Max(X, other.X);
        var sharedHeight = Math.Min(Y + Height, other.Y + other.Height) - Math.Max(Y, other.Y);
        if (sharedWidth <= 0 || sharedHeight <= 0)
        {
            return false;
        }

        var smaller = Math.Min(Width * Height, other.Width * other.Height);
        return sharedWidth * sharedHeight >= (SamePlaceShare * smaller) - Epsilon;
    }

    /// <summary>
    /// Whether two reads are in the same place. A read with no box (an older phone, or a read the phone gave no position) could be
    /// anywhere, so it is never held apart from another read; two boxes are compared with <see cref="SamePlaceAs"/>.
    /// </summary>
    public static bool SamePlace(ReadBox? first, ReadBox? second) =>
        first is not { } a || second is not { } b || a.SamePlaceAs(b);
}

/// <summary>
/// A box as the phone sent it, every member a claim to check. <see cref="Checked"/> is the box when all four are finite, from 0 to 1,
/// the width and height are above 0, and the box ends inside the picture (to within <see cref="ReadBox.EdgeTolerance"/>); else null.
/// </summary>
public sealed record ReadBoxInput(double? X, double? Y, double? Width, double? Height)
{
    public ReadBox? Checked()
    {
        if (X is not { } x || Y is not { } y || Width is not { } width || Height is not { } height)
        {
            return null;
        }

        return IsFraction(x) && IsFraction(y) && IsFraction(width) && IsFraction(height)
            && width > 0 && height > 0
            && x + width <= 1 + ReadBox.EdgeTolerance
            && y + height <= 1 + ReadBox.EdgeTolerance
                ? new ReadBox(x, y, width, height)
                : null;
    }

    private static bool IsFraction(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
