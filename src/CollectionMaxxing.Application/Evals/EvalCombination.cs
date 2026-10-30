using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The virtual engine <c>apple_vision</c>: what the scanner shows on a shelf shot, the Vision text engine's items first and then the
/// picture model's items Vision missed (the two find different spines). It is built here from what the phone uploaded, per photo,
/// so it needs no phone engine of its own, and it is report-only: the M3 gate reads the real engines' rows and the regression
/// check never had its metric keys, so neither moves.
/// </summary>
/// <remarks>
/// A model item is one Vision already has when it is the same item: when both reads have a box, the boxes overlap
/// (<see cref="ReadBox.SamePlaceAs"/>), so the same title on two spines is two items; otherwise, when their best release is the
/// same (its normalised title and year).
/// </remarks>
public static class EvalCombination
{
    /// <summary>The engine name the combination is reported under, next to the three the phone reads.</summary>
    public const string Engine = "apple_vision";

    private const string Ok = "ok";

    /// <summary>
    /// One combined photo for every photo that has both an <c>apple_model</c> and a <c>vision_pipeline</c> row (and a truth file).
    /// A single-item picture (the single set, or a shelf-set photo labelled a single cover or a loose item) is not combined in
    /// the scanner, so its combined row is the model's own.
    /// </summary>
    public static IReadOnlyList<GroundedPhoto> Of(IReadOnlyList<GroundedPhoto> photos, IReadOnlyDictionary<string, TruthEntry> truths)
    {
        var visionRows = new Dictionary<string, GroundedPhoto>(StringComparer.Ordinal);
        foreach (var photo in photos.Where(p => p.Engine == M3Gate.VisionEngine))
        {
            visionRows.TryAdd(photo.PhotoId, photo);
        }

        var combined = new List<GroundedPhoto>();
        foreach (var apple in photos.Where(p => p.Engine == M3Gate.Engine))
        {
            if (truths.TryGetValue(apple.PhotoId, out var truth) && visionRows.TryGetValue(apple.PhotoId, out var vision))
            {
                combined.Add(Combine(apple, vision, IsShelfShot(truth)));
            }
        }

        return combined;
    }

    /// <summary>The scanner combines the two engines on a shelf shot only: a single cover or a loose item is read by the model alone.</summary>
    private static bool IsShelfShot(TruthEntry truth) =>
        truth.Set == GoldenSets.Shelf && truth.ShotKind is not ("single_cover" or "loose_item");

    private static GroundedPhoto Combine(GroundedPhoto apple, GroundedPhoto vision, bool shelf)
    {
        if (!shelf)
        {
            return apple with { Engine = Engine };
        }

        // Both engines have to have answered for their items to be combined; if either did not, the items are the model's own.
        var both = apple.Outcome == Ok && vision.Outcome == Ok;
        var modelAdded = both ? apple.Items.Where(model => !vision.Items.Any(visionItem => SameItem(model, visionItem))).ToList() : [];
        var visionOnly = both && vision.Items.Any(visionItem => !apple.Items.Any(model => SameItem(model, visionItem)));
        return apple with
        {
            Engine = Engine,

            // Vision's items stand as they are, and the model adds what Vision did not find.
            Items = both ? [.. vision.Items, .. modelAdded] : apple.Items,

            // The scanner reads with Vision and then the model, one after the other, so the combined read took both. The first item
            // is Vision's (its own first-item time, or its whole read when it sent none). The outcome and the unreadable count are
            // still the model's.
            LatencyMs = apple.LatencyMs + vision.LatencyMs,
            FirstItemMs = FirstItemOf(apple, vision, both),
            ThermalState = vision.ThermalState,

            // Vision items the model did not read are segments the model did not capture, so the captured count (when the phone
            // sent one) grows by that read.
            CapturedCount = apple.CapturedCount > 0 && visionOnly ? apple.CapturedCount + 1 : apple.CapturedCount,
        };
    }

    /// <summary>
    /// When the combined read first showed an item. Vision's items show first, so it is Vision's first-item time, or its whole read
    /// when it sent none. When Vision found nothing the first item is the model's, after Vision's whole read. When either engine did
    /// not answer, the items are the model's own and so is the time.
    /// </summary>
    private static long? FirstItemOf(GroundedPhoto apple, GroundedPhoto vision, bool both) =>
        !both ? apple.FirstItemMs
        : vision.Items.Count > 0 ? vision.FirstItemMs ?? vision.LatencyMs
        : apple.FirstItemMs is { } modelFirst ? vision.LatencyMs + modelFirst
        : null;

    /// <summary>
    /// Whether a model item and a Vision item are the same item on the shelf. With a box on both, the same item is the same place
    /// (whatever each read there); without one on either side, the same item is the same best release. A read with no release
    /// to compare is never the same item by title.
    /// </summary>
    private static bool SameItem(GroundedItem model, GroundedItem vision) =>
        model.Box is { } modelBox && vision.Box is { } visionBox
            ? modelBox.SamePlaceAs(visionBox)
            : KeyOf(model) is { } key && KeyOf(vision) == key;

    private static (string Title, int? Year)? KeyOf(GroundedItem item) =>
        item.Best is { } best && TitleNormalizer.Normalize(best.Title) is { Length: > 0 } title ? (title, best.Year) : null;
}
