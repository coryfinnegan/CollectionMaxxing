using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The virtual engine <c>apple_vision</c>: what the scanner shows on a shelf shot, the Vision text engine's items first and then the
/// picture model's items Vision missed (the two find different spines). It is built here from what the phone uploaded, per photo,
/// so it needs no phone engine of its own. It is the row the M3 gate measures (<see cref="M3Gate.MeasuredEngine"/>, ADR-032), because
/// it is what the app shows. The regression check reads it like any engine under its own metric keys, which a baseline taken before
/// it existed does not have (a metric the baseline does not know is new, not a regression).
/// </summary>
/// <remarks>
/// <para>
/// A model item is one Vision already has when it is the same item: when both reads have a box, the boxes overlap
/// (<see cref="ReadBox.SamePlaceAs"/>), so the same title on two spines is two items; otherwise, when their best release is the
/// same (its normalised title and year).
/// </para>
/// <para>
/// A Vision read that names no kind takes the kind the model read at the same place, as the scanner lends it before it sends the snap
/// (<c>filledFrom</c>, combine.ts): without that, the combined row reported a tape as the kindless read it arrived as, while the app
/// searched it as the film the model called it.
/// </para>
/// <para>
/// When the model fails on a shelf (a timeout, an error or a refusal), the scanner keeps Vision's reading, so the combined row is
/// Vision's items too (it scored 0 of 46 on a shelf where Vision alone scored 23, before). That row is Vision's reading with the
/// model's failure as its <c>FellBackFrom</c>, the way the scanner records such a snap: it is not a timeout, error or refusal of
/// its own, the model row reports those; its total time still includes the wait for the model. When Vision failed and the model answered, the items are the model's.
/// </para>
/// </remarks>
public static class EvalCombination
{
    /// <summary>The engine name the combination is reported under, next to the three the phone reads.</summary>
    public const string Engine = "apple_vision";

    private const string Ok = "ok";
    private const string Timeout = "timeout";
    private const string Refused = "refused";

    /// <summary>
    /// One combined photo for every photo that has both an <c>apple_model</c> and a <c>vision_pipeline</c> row (and a truth file).
    /// A single-item picture (the single set, or a shelf-set photo labelled a single cover or a loose item) is not combined in
    /// the scanner, so its combined row is the model's own row whole (items, tiers, cover cue, time, thermal state).
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

        // The model failed and Vision read: the scanner shows Vision's reading, and the model's failure is the fellBackFrom.
        if (apple.Outcome != Ok && vision.Outcome == Ok && vision.Items.Count > 0)
        {
            return VisionAfterTheModelFailed(apple, vision);
        }

        // Both engines have to have answered for their items to be combined; if either did not, the items are the model's own.
        var both = apple.Outcome == Ok && vision.Outcome == Ok;

        // What the model adds beside Vision's items is unconfirmed, as in the scanner: nothing else saw it, so it is review (T2), never an
        // auto-add, whatever tier the phone claimed. Vision's items, and every other row (a single, one engine failing), are not marked.
        var modelAdded = both
            ? apple.Items.Where(model => !vision.Items.Any(visionItem => SameItem(model, visionItem))).Select(model => model with { Unconfirmed = true }).ToList()
            : [];
        var visionOnly = both && vision.Items.Any(visionItem => !apple.Items.Any(model => SameItem(model, visionItem)));
        return apple with
        {
            Engine = Engine,

            // Vision's items stand as they are but for the kind the model read at the same place, and the model adds what Vision did not find.
            Items = both ? [.. vision.Items.Select(visionItem => KindLentFrom(visionItem, apple.Items)), .. modelAdded] : apple.Items,

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
    /// The scanner's snap when the model failed on a shelf and Vision read: Vision's reading stands as it was (its items, outcome,
    /// unreadable and captured counts, so its segments still reconcile) and the model's failure is the <c>FellBackFrom</c>: the
    /// reason the phone sent, else the one its outcome says (a timeout, a refusal is a declined, an error is unavailable). The first item is
    /// Vision's first, or the end of its whole read when it sent none; the total is Vision's read plus the model's, the time it
    /// waited before failing, since the snap is not finished until the model gives up (the same sum as the normal combined row).
    /// Vision's OCR lines belong to its own row, and the model's duplicate re-scan evidence
    /// is used only when Vision sent none.
    /// </summary>
    private static GroundedPhoto VisionAfterTheModelFailed(GroundedPhoto apple, GroundedPhoto vision) =>
        vision with
        {
            Engine = Engine,
            FellBackFrom = apple.FellBackFrom ?? FellBackFromOutcome(apple.Outcome),

            // The snap is not finished until the model gives up, so the total is Vision's read and then the model's wait, as in the
            // normal combined row; the first item is still Vision's.
            LatencyMs = vision.LatencyMs + apple.LatencyMs,
            FirstItemMs = vision.FirstItemMs ?? vision.LatencyMs,
            DuplicateRescan = vision.DuplicateRescan ?? apple.DuplicateRescan,
            OcrLines = null,
            OcrPieces = null,
            CoverCue = null,
        };

    /// <summary>Why a model row that sent no reason failed, in the scanner's words: a refusal is declined, a timeout a timeout, anything else unavailable.</summary>
    private static string FellBackFromOutcome(string outcome) => EnumText.ToText(
        outcome switch
        {
            Timeout => FallbackReason.Timeout,
            Refused => FallbackReason.Declined,
            _ => FallbackReason.Unavailable,
        });

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

    /// <summary>
    /// The Vision read with the kind the model gave the same spine, as the scanner lends it (<c>filledFrom</c>, combine.ts): a Vision
    /// read names no kind, so the model's kind is what the app would have searched that spine with, and the combined row says so.
    /// Only the kind, only onto a read that named none, only from a read that named one, and only where both read the same place: a
    /// read with no box sits nowhere, so nothing is lent across the shelf by a title that happens to match (as <c>placeOf</c> has it).
    /// This row is already grounded, so lending changes what the run reports the read as, never which releases were found for it.
    /// </summary>
    private static GroundedItem KindLentFrom(GroundedItem vision, IReadOnlyList<GroundedItem> model) =>
        vision.Read is { Kind: null } read && vision.Box is { } visionBox
        && model.FirstOrDefault(m => m.Box is { } modelBox && modelBox.SamePlaceAs(visionBox))?.Read?.Kind is { } kind
            ? vision with { Read = read with { Kind = kind } }
            : vision;

    private static (string Title, int? Year)? KeyOf(GroundedItem item) =>
        item.Best is { } best && TitleNormalizer.Normalize(best.Title) is { Length: > 0 } title ? (title, best.Year) : null;
}
