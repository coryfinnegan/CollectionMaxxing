using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Api.Evals;

/// <summary>The phone that ran the evals.</summary>
public sealed record EvalDeviceRequest(string? Model = null, string? OsVersion = null, string? AppVersion = null);

/// <summary>
/// One item the phone read in a golden photo: the fields of an <c>/identify</c> item (every one a claim to check, never a verdict),
/// plus <c>ResolvedBy</c>, the step that settled it (model, vision_text, artwork or fix_it). When it is left out, the engine's own step is assumed.
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
    ResolvedStep? ResolvedBy = null);

/// <summary>One golden photo run through one engine on the phone.</summary>
public sealed record EvalPhotoResult(
    string? PhotoId = null,
    ScanEngine? Engine = null,
    EvalOutcome? Outcome = null,
    FallbackReason? FellBackFrom = null,
    IReadOnlyList<EvalReadItem>? Items = null,
    int? UnreadableCount = null,
    long? LatencyMs = null,
    long? FirstItemMs = null,
    ThermalState? ThermalState = null);

/// <summary>Body of <c>POST /api/admin/evals/runs</c>: at most 2 MB. Nothing in it is an image.</summary>
public sealed record EvalRunRequest(EvalDeviceRequest? Device = null, string? ConfigVersion = null, IReadOnlyList<EvalPhotoResult>? Results = null);
