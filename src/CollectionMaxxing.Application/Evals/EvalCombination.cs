using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The virtual engine <c>apple_vision</c>: what the scanner reads on a shelf shot, the picture model's items plus the Vision
/// text engine's items the model did not find (the two find different spines). It is built here from what the phone uploaded,
/// per photo, so it needs no phone engine of its own, and it is report-only: the M3 gate reads the real engines' rows and the
/// regression check never had its metric keys, so neither moves.
/// </summary>
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

        var added = apple.Outcome == Ok && vision.Outcome == Ok ? NotReadByTheModel(apple.Items, vision.Items) : [];
        return apple with
        {
            Engine = Engine,
            Items = added.Count == 0 ? apple.Items : [.. apple.Items, .. added],

            // The scanner reads with Vision after the model, so the combined read took both: an upper bound, since they could overlap.
            // The first item is the model's (Vision's come after), and so are the outcome and the unreadable count.
            LatencyMs = apple.LatencyMs + vision.LatencyMs,
            ThermalState = vision.ThermalState,

            // Added items are segments the model did not capture, so the captured count (when the phone sent one) grows by that read.
            CapturedCount = apple.CapturedCount > 0 && added.Count > 0 ? apple.CapturedCount + 1 : apple.CapturedCount,
        };
    }

    /// <summary>
    /// The Vision items whose best release is not the best release of a model item (its normalised title and year). A Vision
    /// item with no release to compare is left out; the model's items are all kept, whatever they found.
    /// </summary>
    private static List<GroundedItem> NotReadByTheModel(IReadOnlyList<GroundedItem> model, IReadOnlyList<GroundedItem> vision)
    {
        var read = model.Select(KeyOf).Where(key => key is not null).Select(key => key!.Value).ToHashSet();
        return [.. vision.Where(item => KeyOf(item) is { } key && !read.Contains(key))];
    }

    private static (string Title, int? Year)? KeyOf(GroundedItem item) =>
        item.Best is { } best && TitleNormalizer.Normalize(best.Title) is { Length: > 0 } title ? (title, best.Year) : null;
}
