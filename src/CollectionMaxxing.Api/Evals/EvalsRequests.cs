using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Api.Evals;

/// <summary>The phone that ran the evals.</summary>
public sealed record EvalDeviceRequest(string? Model = null, string? OsVersion = null, string? AppVersion = null);

/// <summary>
/// One item the phone read in a golden photo: the fields of an <c>/identify</c> item (every one a claim to check, never a verdict),
/// plus <c>ResolvedBy</c>, the step that settled it (model, vision_text, artwork or fix_it). When it is left out, the engine's own step is assumed.
/// <c>Tier</c> is the commit tier the pipeline assigned (T0, T1, T2); <c>Provisional</c> with <c>IsGuess</c> marks a kept guess (docs/16 §17).
/// </summary>
public sealed record EvalReadItem(
    string? LocalId = null,
    string? Text = null,
    string? Title = null,
    string? Creator = null,
    string? Edition = null,
    ItemCompleteness? Completeness = null,
    WorkKind? Kind = null,
    ReleaseFormat? Format = null,
    int? Year = null,
    string? Platform = null,
    ResolvedStep? ResolvedBy = null,
    ScanTier? Tier = null,
    bool? Provisional = null,
    bool? IsGuess = null,
    string? SegmentId = null);

/// <summary>
/// Reads an engine as its API name only (<c>apple_model</c>, <c>fm_text</c> or <c>vision_pipeline</c>). The stock enum converter would also take a number
/// (7 would become an engine nobody has) or a C# member name; a result file's engine key must be one of the three real engines. The run endpoint puts it first in its reader's converters.
/// </summary>
public sealed class StrictScanEngineConverter : JsonConverter<ScanEngine>
{
    public override ScanEngine Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            foreach (var engine in Enum.GetValues<ScanEngine>())
            {
                if (string.Equals(EnumText.ToText(engine), text, StringComparison.Ordinal))
                {
                    return engine;
                }
            }
        }

        throw new JsonException("The engine must be apple_model, fm_text or vision_pipeline.");
    }

    public override void Write(Utf8JsonWriter writer, ScanEngine value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EnumText.ToText(value));
}

/// <summary>What a duplicate re-scan of the photo's shelf showed: owned hits, how many answered on-card, unsolicited new items.</summary>
public sealed record EvalDuplicateRescan(int OwnedHits = 0, int AnsweredOnCard = 0, int UnsolicitedAdds = 0);

/// <summary>One cleaned OCR line the Vision engine split: its text (up to 200 characters) and its box, each value 0 to 1 with the origin at the top left.</summary>
public sealed record EvalOcrLine(string? Text = null, double? X = null, double? Y = null, double? Width = null, double? Height = null);

/// <summary>
/// One golden photo run through one engine on the phone. <c>CapturedCount</c> is how many segments the phone captured (reads plus
/// unreadable must reconcile to it); <c>CapturedAt</c> stamps the capture for the wall-clock metrics; <c>DuplicateRescan</c> carries
/// a duplicate re-scan's evidence, when the photo was one (docs/16 §17). <c>OcrLines</c> (at most 1000) and <c>OcrPieces</c> (for each
/// piece the Vision engine made, the indexes of its lines) come from the <c>vision_pipeline</c> photo only, so a later fix can use the
/// real lines; the PC keeps them with the grounded run and ignores them on any other engine's photo.
/// </summary>
public sealed record EvalPhotoResult(
    string? PhotoId = null,
    ScanEngine? Engine = null,
    EvalOutcome? Outcome = null,
    FallbackReason? FellBackFrom = null,
    IReadOnlyList<EvalReadItem>? Items = null,
    int? UnreadableCount = null,
    long? LatencyMs = null,
    long? FirstItemMs = null,
    ThermalState? ThermalState = null,
    int? CapturedCount = null,
    DateTimeOffset? CapturedAt = null,
    EvalDuplicateRescan? DuplicateRescan = null,
    IReadOnlyList<EvalOcrLine>? OcrLines = null,
    IReadOnlyList<IReadOnlyList<int>>? OcrPieces = null);

/// <summary>Body of <c>POST /api/admin/evals/runs</c>: at most 2 MB. Nothing in it is an image. <c>Partial</c> is true when the phone cancelled the run part way. <c>BaselineNote</c> explains a deliberate threshold move (docs/16 §17).</summary>
public sealed record EvalRunRequest(
    EvalDeviceRequest? Device = null,
    string? ConfigVersion = null,
    IReadOnlyList<EvalPhotoResult>? Results = null,
    bool? Partial = null,
    string? BaselineNote = null);
