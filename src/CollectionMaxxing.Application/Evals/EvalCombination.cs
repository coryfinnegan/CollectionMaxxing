using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
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
/// One place holds one item, whatever either engine read there (<see cref="ReadPlaces"/>, the rule <c>/identify</c> applies to
/// what the scanner sends): where both engines read a place, the read that grounds better is the row shown (Matched beats
/// Ambiguous, which beats a read that found nothing, then the higher score, then Vision's own, which comes first), and the
/// other is no second row for that spine. So the same title on two spines is still two items, and a right read the model made
/// is no longer thrown away because Vision read the author's name or a sticker at that spot. A Vision piece that holds several
/// model reads is several spines merged into one piece: it loses its place and each model read inside it stands.
/// Only when a box is missing on either side, where there is no place to go on, is a model read the same item as a Vision read
/// because their best release is the same (its normalised title and year), as it was before boxes.
/// </para>
/// <para>
/// Where the read that stands at a place is the model's own and the Vision read it beat there named the same release, the two
/// engines agreeing is itself the confirmation the model-only mark exists to stand in for, so the row is not marked: a person
/// sees it ticked, as a Vision row always was. A different release at that place is still the contest above, untouched.
/// </para>
/// <para>
/// A Vision read that names no kind takes the kind the model read at its place, as the scanner lends it before it sends the snap
/// (<c>filledFrom</c>, combine.ts): without that, the combined row reported a tape as the kindless read it arrived as, while the app
/// searched it as the film the model called it. A model-only place is grounded with the shelf's printed kind lent, the same way:
/// the app sends the model's and Vision's reads of a snap together, so <c>/identify</c> takes a print-denied kind off either one
/// before it searches (<see cref="GroundedPhoto.ItemsLentPrint"/>); the apple_model row the phone uploaded is grounded alone, and
/// stays that way, because it measures the model by itself.
/// </para>
/// <para>
/// A model-only read, one that is the same item as no Vision read (no Vision item at its place; with no box on either side, none of
/// the same release), is shown only when the shelf's own print has its words (<see cref="PrintSupport"/>), as the app adds it only
/// then: the model invents titles, and one that shares nothing with what Vision read on the shelf is not on it. The print is every
/// word of every OCR line on the <c>vision_pipeline</c> row (<see cref="GroundedPhoto.OcrLines"/>); the read's title (what the model
/// read, not a release's) needs at least two thirds of its words, stop words and one-letter words not counted, to be among them,
/// allowing a plural or one wrong letter in a word of five letters or more. A model read at a Vision item's place stays the place's
/// contest above, and where Vision failed or sent no lines (a run from before the phone sent them, or a shelf with no text) there
/// is no print and every model read stands, as it did before.
/// </para>
/// <para>
/// When the model fails on a shelf (a timeout, an error or a refusal), the scanner keeps Vision's reading, so the combined row is
/// Vision's items too (it scored 0 of 46 on a shelf where Vision alone scored 23, before). That row is Vision's reading with the
/// model's failure as its <c>FellBackFrom</c>, the way the scanner records such a snap: it is not a timeout, error or refusal of
/// its own, the model row reports those; its total time still includes the wait for the model. When Vision failed and the model answered, the items are the model's.
/// </para>
/// <para>
/// The captured segments the phone counted are counted here only while a read of them stands in the shown items: the model's while at
/// least one model read stands, Vision's while at least one Vision read stands. Since a title only the model read is dropped when the
/// print does not back it, a shelf can show none of the model's reads; the shot was captured once, Vision's reads account for it, and
/// the scanner deliberately shows no row for the unbacked read, so the dropped read's segment is not a gap. A segment with standing
/// reads is still counted, and one the phone counted that carries no read at all is still reported as unaccounted for.
/// </para>
/// <para>
/// A single cover or loose item is read by the model alone (<c>EngineSelector.read</c>), and when the model fails there (a timeout
/// with nothing read, a refusal, a context overflow or an error: <c>ModelUnusableError</c> and the catch around the picture read) the
/// scanner tries the text tier (<c>fm_text</c>) and then Vision's own reading. So the combined row of such a photo is the <c>fm_text</c>
/// row when it answered, else the <c>vision_pipeline</c> row when it answered, either with the model's failure as its
/// <c>FellBackFrom</c> and the model row's own time as its time (the phone times <c>selector.read</c>, which runs the fallback inside
/// that call, so it is already the whole wait); only when neither answered is it the model's failure,
/// as the model's own row says. A model that answered, even with nothing, is the combined row whole. The gate's
/// <c>model_timeouts</c> still reads the model's own row.
/// </para>
/// </remarks>
public static class EvalCombination
{
    /// <summary>The engine name the combination is reported under, next to the three the phone reads.</summary>
    public const string Engine = "apple_vision";

    private const string TextEngine = "fm_text";
    private const string Ok = "ok";
    private const string Timeout = "timeout";
    private const string Refused = "refused";
    private const string MatchedVerdict = "matched";
    private const string AmbiguousVerdict = "ambiguous";
    private const string PendingVerdict = "pending";

    /// <summary>
    /// One combined photo for every photo that has both an <c>apple_model</c> and a <c>vision_pipeline</c> row (and a truth file).
    /// A single-item picture (the single set, or a shelf-set photo labelled a single cover or a loose item) is not combined in
    /// the scanner, so its combined row is the model's own row whole (items, tiers, cover cue, time, thermal state); when the model
    /// failed on it, the row of the tier the scanner fell back to (<see cref="AfterTheModelFellThrough"/>).
    /// </summary>
    public static IReadOnlyList<GroundedPhoto> Of(IReadOnlyList<GroundedPhoto> photos, IReadOnlyDictionary<string, TruthEntry> truths)
    {
        var visionRows = new Dictionary<string, GroundedPhoto>(StringComparer.Ordinal);
        foreach (var photo in photos.Where(p => p.Engine == M3Gate.VisionEngine))
        {
            visionRows.TryAdd(photo.PhotoId, photo);
        }

        var textRows = new Dictionary<string, GroundedPhoto>(StringComparer.Ordinal);
        foreach (var photo in photos.Where(p => p.Engine == TextEngine))
        {
            textRows.TryAdd(photo.PhotoId, photo);
        }

        var combined = new List<GroundedPhoto>();
        foreach (var apple in photos.Where(p => p.Engine == M3Gate.Engine))
        {
            if (truths.TryGetValue(apple.PhotoId, out var truth) && visionRows.TryGetValue(apple.PhotoId, out var vision))
            {
                combined.Add(Combine(apple, textRows.GetValueOrDefault(apple.PhotoId), vision, IsShelfShot(truth)));
            }
        }

        return combined;
    }

    /// <summary>The scanner combines the two engines on a shelf shot only: a single cover or a loose item is read by the model alone.</summary>
    private static bool IsShelfShot(TruthEntry truth) =>
        truth.Set == GoldenSets.Shelf && truth.ShotKind is not ("single_cover" or "loose_item");

    private static GroundedPhoto Combine(GroundedPhoto apple, GroundedPhoto? text, GroundedPhoto vision, bool shelf)
    {
        if (!shelf)
        {
            return apple.Outcome == Ok ? apple with { Engine = Engine } : AfterTheModelFellThrough(apple, text, vision);
        }

        // The model failed and Vision read: the scanner shows Vision's reading, and the model's failure is the fellBackFrom.
        if (apple.Outcome != Ok && vision.Outcome == Ok && vision.Items.Count > 0)
        {
            return VisionAfterTheModelFailed(apple, vision);
        }

        // Both engines have to have answered for their items to be combined; if either did not, the items are the model's own.
        var both = apple.Outcome == Ok && vision.Outcome == Ok;

        // The model's items for the combination: the ones grounded with the shelf's printed kind lent, when the vision_pipeline row
        // named one (ItemsLentPrint), else this row's own. A model-only place is then grounded the way /identify grounds it, under
        // one snap with Vision.
        var modelItems = apple.ItemsLentPrint ?? apple.Items;

        // The shelf's own print: every word of every line Vision read on the shot. A run from before the phone sent its lines, or a shelf
        // Vision read no text on, has no print to check against, so every model read stands, as it did before.
        var printed = both && vision.OcrLines is { Count: > 0 } lines ? PrintSupport.PrintedWords(lines.Select(line => line.Text)) : null;
        var shown = both ? Settled(WithKindsLent(vision.Items, modelItems, EnumText.ParseOrNull<WorkKind>(vision.PrintKind)), modelItems, printed) : null;
        return apple with
        {
            Engine = Engine,

            // Vision's reads and the model's, one item per place: the read of a place that grounds better is the row shown, and a
            // Vision read that named no kind carries the one the model gave its place.
            Items = shown?.Items ?? modelItems,

            // The scanner reads with Vision and then the model, one after the other, so the combined read took both. The first item
            // is Vision's (its own first-item time, or its whole read when it sent none). The outcome and the unreadable count are
            // still the model's.
            LatencyMs = apple.LatencyMs + vision.LatencyMs,
            FirstItemMs = FirstItemOf(apple, vision, both),
            ThermalState = vision.ThermalState,

            // The segments the shown items carry (when the phone sent a count): the model's while a model read stands, Vision's while
            // a Vision read stands. Either engine failing, the items are the model's own and so is the count.
            CapturedCount = shown is null ? apple.CapturedCount : CapturedOf(apple, shown),

            // The lent regrounding is already folded into Items above; the combined row carries neither it nor a print of its own.
            ItemsLentPrint = null,
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
            PrintKind = null,
        };

    /// <summary>
    /// The scanner's snap when the model failed on a single cover (<c>EngineSelector.read</c>): the text tier reads what Vision's text
    /// found (<c>fm_text</c>), and when that fails too Vision's own reading stands. A model that answers, even with nothing, is not a
    /// failure, and a timeout that kept items is an answer: only a timeout with nothing read, a refusal, a context overflow or an
    /// error falls through (<c>ModelUnusableError</c> and the catch around the picture read). The fallback row is the snap whole, with the
    /// model's failure as its <c>FellBackFrom</c> (the reason the phone sent, else the one its outcome says), and the total is the
    /// model row's own time: the phone times the whole <c>selector.read</c> call, which runs the fallback inside it, so adding the
    /// fallback row's time would count it twice. When items were read the first item is shown by the end of that read (no earlier time
    /// is known). When neither fallback row answered the combined row is the model's own failure, as it was. The gate's
    /// <c>model_timeouts</c> still reads the model's own row.
    /// </summary>
    private static GroundedPhoto AfterTheModelFellThrough(GroundedPhoto apple, GroundedPhoto? text, GroundedPhoto vision)
    {
        var fallback = text is { Outcome: Ok } ? text : vision.Outcome == Ok ? vision : null;
        if (fallback is null)
        {
            return apple with { Engine = Engine };
        }

        return fallback with
        {
            Engine = Engine,
            FellBackFrom = apple.FellBackFrom ?? FellBackFromOutcome(apple.Outcome),
            LatencyMs = apple.LatencyMs,
            FirstItemMs = fallback.Items.Count > 0 ? apple.LatencyMs : null,
            DuplicateRescan = fallback.DuplicateRescan ?? apple.DuplicateRescan,

            // Vision's OCR lines belong to its own row.
            OcrLines = null,
            OcrPieces = null,
        };
    }

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
    /// The shelf's items: every read of the picture, one per place. A model read with no box to be told apart by is the Vision
    /// read it names the same release as, read again, so it is dropped as it was before boxes; every other model read is a read
    /// of the picture in its own right, marked unconfirmed, because nothing else saw that title: it is review (T2), never an
    /// auto-add, whatever tier the phone claimed. The places are then settled by <see cref="ReadPlaces.Weaker"/>, Vision's reads
    /// first so its own read stands where the model read no better. Where the model read stands against a same-place Vision
    /// read of the same release (its normalised title and year), the two engines agreeing there is itself the confirmation the
    /// mark stands in for, so it is not marked; a different release at that place is still a contest between the two and is
    /// marked as it was. Vision's items, and every other row (a single, one engine failing), are not marked.
    /// A model-only read, one that is the same item as no Vision read (<see cref="SameItem"/>: no Vision item at its place, or with
    /// no place to go on, none of the same release), is shown only when the shelf's print backs up its title
    /// (<see cref="PrintSupport.Supports"/>, <paramref name="printed"/>), as the app adds it only then. A read at a Vision item's
    /// place is that place's contest as above, whatever it printed. With no <paramref name="printed"/>, every read stands.
    /// </summary>
    private static Shown Settled(List<GroundedItem> vision, IReadOnlyList<GroundedItem> model, IReadOnlySet<string>? printed)
    {
        var modelReads = model
            .Where(read => !vision.Any(visionItem => SameReleaseWithNoPlace(read, visionItem)))
            .Where(read => printed is null || vision.Any(visionItem => SameItem(read, visionItem)) || PrintSupport.Supports(read.ReadTitle, printed))
            .ToList();
        var reads = new List<GroundedItem>(vision.Count + modelReads.Count);
        reads.AddRange(vision);
        reads.AddRange(modelReads.Select(read => read with { Unconfirmed = true }));

        var placed = new List<PlacedRead>(reads.Count);
        placed.AddRange(vision.Select(read => Placed(read, onlyTheModelRead: false)));
        placed.AddRange(modelReads.Select(read => Placed(read, onlyTheModelRead: true)));

        var weaker = ReadPlaces.Weaker(placed).ToHashSet();
        var confirmed = ReadPlaces.Confirmed(placed).ToHashSet();
        var kept = new List<GroundedItem>(reads.Count - weaker.Count);
        var visionStands = false;
        var modelStands = false;
        for (var i = 0; i < reads.Count; i++)
        {
            if (weaker.Contains(i))
            {
                continue;
            }

            // The reads are Vision's first, then the model's: which engine a standing read is from is where it sat in that list.
            visionStands |= i < vision.Count;
            modelStands |= i >= vision.Count;
            kept.Add(confirmed.Contains(i) ? reads[i] with { Unconfirmed = false } : reads[i]);
        }

        return new Shown(kept, visionStands, modelStands);
    }

    /// <summary>The shelf's items, and whether any read of each engine is among them (a segment is accounted for only by a read that stands).</summary>
    private sealed record Shown(List<GroundedItem> Items, bool VisionStands, bool ModelStands);

    /// <summary>
    /// The captured segments of the shelf as it is shown. The phone counts what each engine captured (a read is one segment, shared by
    /// its items, plus the spines it called unreadable), and the combined row reports the model's unreadable spines as the snap does. A
    /// segment is counted only while a read of it stands in the shown items: the model's when at least one model read stands, Vision's
    /// when at least one Vision read stands. A segment whose every read was dropped (a model title the print does not back, a read that
    /// lost its place) has left the shelf, and the scorer, which counts the segments the shown items carry, would otherwise report a
    /// gap the scanner never had. A segment the phone counted that carries no read at all is still a gap, and is still reported.
    /// 0 stays 0: the phone sent no count, so there is nothing to reconcile.
    /// </summary>
    private static int CapturedOf(GroundedPhoto apple, Shown shown)
    {
        if (apple.CapturedCount <= 0)
        {
            return apple.CapturedCount;
        }

        var modelSegments = Math.Max(0, apple.CapturedCount - apple.UnreadableCount);
        return apple.UnreadableCount + (shown.ModelStands ? modelSegments : 0) + (shown.VisionStands ? 1 : 0);
    }

    /// <summary>
    /// A read as the place rule settles it: where it sat, how well it grounds, the score of its best release (its normalised
    /// title and year, for <see cref="ReadPlaces.Confirmed"/>), and which engine read it.
    /// </summary>
    private static PlacedRead Placed(GroundedItem item, bool onlyTheModelRead) =>
        new(item.Box, GroundingOf(item.Verdict), item.Best?.Score ?? 0, onlyTheModelRead, BookOf(item));

    /// <summary>The release a read's best option names, its normalised title and year, as a single key; null when it has none.</summary>
    private static string? BookOf(GroundedItem item) => KeyOf(item) is { } key ? $"{key.Title}\0{key.Year}" : null;

    /// <summary>The verdict the phone's run recorded, on the ladder a place is settled by. A verdict still pending grounds nothing yet.</summary>
    private static ReadGrounding GroundingOf(string verdict) => verdict switch
    {
        MatchedVerdict => ReadGrounding.Matched,
        AmbiguousVerdict => ReadGrounding.Ambiguous,
        PendingVerdict => ReadGrounding.Unsettled,
        _ => ReadGrounding.Nothing,
    };

    /// <summary>
    /// Whether a model read and a Vision read are one read with no place to tell them apart: a box missing on either side, and the
    /// same best release (its normalised title and year). A read with no release to compare is never the same read by title.
    /// </summary>
    private static bool SameReleaseWithNoPlace(GroundedItem model, GroundedItem vision) =>
        (model.Box is null || vision.Box is null) && KeyOf(model) is { } key && KeyOf(vision) == key;

    /// <summary>
    /// Whether a model item and a Vision item are the same item on the shelf. With a box on both, the same item is the same place
    /// (whatever each read there); without one on either side, the same item is the same best release. A read with no release
    /// to compare is never the same item by title. Only the print check asks this (a read at a Vision item's place is not checked):
    /// what the shelf shows is settled one place at a time (<see cref="Settled"/>).
    /// </summary>
    private static bool SameItem(GroundedItem model, GroundedItem vision) =>
        model.Box is { } modelBox && vision.Box is { } visionBox
            ? modelBox.SamePlaceAs(visionBox)
            : KeyOf(model) is { } key && KeyOf(vision) == key;

    /// <summary>
    /// Vision's items with the kind the model gave each spine, as the scanner lends it (<c>filledFrom</c>, combine.ts): a Vision read
    /// names no kind, so the model's kind is what the app searched that spine with, and the combined row says so. Only the kind, only
    /// onto a read that named none, and only from a read that named one, and never a kind <paramref name="printKind"/> denies while it
    /// speaks (the one rule <c>IdentifyCleaning</c> applies, <see cref="KindContext.PrintThatSpeaks"/> and
    /// <see cref="KindContext.InFamily"/>): a model row grounded without the print still names the kind it guessed, and lending that
    /// kind here, while no read of the snap already tells that kind apart, would show a read as the very kind <c>/identify</c> would
    /// have taken off it. The place decides, the way the phone's <c>placeOf</c> decides: each model read fills the one Vision item its
    /// box overlaps the most, so a single wide model box over two spines lends its kind to one of them, not to both. A read with no
    /// box sits nowhere and neither lends nor takes, so nothing travels across the shelf on a title that happens to match. The phone
    /// also asks that the Vision read say everything the model's read says (<c>lendsTo</c>), which needs the words of both reads; the
    /// run keeps only the text each read searched with, so the place is all this has to go on and it can lend where the phone would
    /// not. That costs nothing: this row is already grounded, so lending changes what the run reports a read as, never which releases
    /// were found for it or how the place was settled.
    /// </summary>
    private static List<GroundedItem> WithKindsLent(IReadOnlyList<GroundedItem> vision, IReadOnlyList<GroundedItem> model, WorkKind? printKind)
    {
        // Whether the print speaks is settled on every read's own kind, before any one of them takes the model's.
        var named = vision.Concat(model).Select(i => EnumText.ParseOrNull<WorkKind>(i.Read?.Kind));
        var speaking = KindContext.PrintThatSpeaks(printKind, named);

        var items = vision.ToList();
        foreach (var read in model)
        {
            if (read.Read?.Kind is not { } kindText || read.Box is not { } box)
            {
                continue;
            }

            if (speaking is { } printed && EnumText.ParseOrNull<WorkKind>(kindText) is { } kind && kind != WorkKind.Other
                && !KindContext.InFamily(kind, printed))
            {
                continue;
            }

            var at = PlaceOf(box, items);
            if (at >= 0 && items[at].Read is { Kind: null } kindless)
            {
                items[at] = items[at] with { Read = kindless with { Kind = kindText } };
            }
        }

        return items;
    }

    /// <summary>
    /// Which of <paramref name="items"/> the box sits on: the one it overlaps the most, and only when the two are one place
    /// (<see cref="ReadBox.SamePlaceAs"/>). -1 when it sits on none, and for an item with no box, which sits nowhere.
    /// </summary>
    private static int PlaceOf(ReadBox box, List<GroundedItem> items)
    {
        var best = -1;
        var share = 0d;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Box is { } other && other.SamePlaceAs(box) && box.OverlapShare(other) > share)
            {
                best = i;
                share = box.OverlapShare(other);
            }
        }

        return best;
    }

    private static (string Title, int? Year)? KeyOf(GroundedItem item) =>
        item.Best is { } best && TitleNormalizer.Normalize(best.Title) is { Length: > 0 } title ? (title, best.Year) : null;
}
