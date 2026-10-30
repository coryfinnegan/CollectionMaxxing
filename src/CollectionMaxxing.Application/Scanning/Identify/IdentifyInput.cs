using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// One item as the phone sent it (the API maps its request to this). Nothing here is an image. <c>Box</c> is where the read sits in
/// the picture (x, y, width, height, each 0 to 1, origin top left); an older phone, or a read with no position, sends none. A box
/// is a hint: one that cannot be used (see <see cref="ReadBoxInput.Clamped"/>) is the same as none and never refuses the read.
/// </summary>
public sealed record IdentifyItemInput(
    string? LocalId,
    string? SegmentId,
    string? Text,
    string? Title,
    string? Creator,
    string? Edition,
    ItemCompleteness? Completeness,
    WorkKind? Kind,
    ReleaseFormat? Format,
    int? Year,
    string? Platform,
    Guid? LocalMatchReleaseId,
    string? ProductCode = null,
    ReadBoxInput? Box = null);

/// <summary>
/// What the phone read from one photo. <c>Kind</c> is a shelf read unless the phone says cover; <c>BoundCandidateId</c> ties
/// a cover read to the T2 row it was shot for (design §8(b)), which is then upgraded in place instead of adding candidates;
/// <c>BoundItemId</c> ties an edition-proof read to the library item it verifies (design §11), which is upgraded in place
/// on a match and never touched otherwise.
/// </summary>
public sealed record IdentifyInput(
    Guid? SnapId,
    SnapTrigger? Trigger,
    ScanEngine? Engine,
    FallbackReason? FellBackFrom,
    IReadOnlyList<IdentifyItemInput>? Items,
    ShotKind? ShotKind = null,
    IdentifyKind Kind = IdentifyKind.Shelf,
    Guid? BoundCandidateId = null,
    Guid? BoundItemId = null);

public static class IdentifyErrors
{
    public static readonly DomainError SnapInUse =
        new(ErrorKind.Conflict, "scan.snap_in_use", "That snapId was already used. Send a new snapId for a new picture.");
}

/// <summary>An item after cleaning: the facts to ground, the phone's claim about a known release, the profile chosen for it, and where it sits (null when the phone said nothing).</summary>
internal sealed record CleanItem(string? LocalId, string? SegmentId, ReadFacts Facts, Guid? LocalMatchReleaseId, ScoringProfileKind Profile, ReadBox? Box = null);

/// <summary>Validates and cleans what the phone sent: limits, control characters, lengths.</summary>
internal static class IdentifyCleaning
{
    public const int MaxItems = 40;
    public const int MaxItemBytes = 16 * 1024;
    public const int MaxTextLength = 2000;
    public const int MaxFieldLength = 500;
    public const int MaxLocalIdLength = 100;
    public const int MaxSegmentIdLength = 100;
    public const int MaxPlatformLength = 100;
    private const int MinYear = 1000;
    private const int MaxYear = 2200;

    public static DomainError? Validate(IdentifyInput input)
    {
        if (input.SnapId is not { } snap || snap == Guid.Empty)
        {
            return ScanningErrors.Invalid("scan.snap_id_required", "Send a snapId for this picture.", "snapId");
        }

        if (input.Trigger is null)
        {
            return ScanningErrors.Invalid("scan.trigger_required", "Send the trigger: auto, shutter or import.", "trigger");
        }

        if (input.Engine is null)
        {
            return ScanningErrors.Invalid("scan.engine_required", "Send the engine that read the picture.", "engine");
        }

        if (input.Items is not { Count: > 0 })
        {
            return ScanningErrors.Invalid("scan.items_required", "Send at least one item.", "items");
        }

        if (input.Items.Count > MaxItems)
        {
            return ScanningErrors.Invalid("scan.too_many_items", $"Send at most {MaxItems} items at a time.", "items");
        }

        if (input is { BoundCandidateId: not null, BoundItemId: not null })
        {
            return ScanningErrors.Invalid(
                "scan.bound_one_row", "A read belongs to one row: send a candidate or an item, not both.", "itemId");
        }

        if (input.BoundCandidateId is not null)
        {
            if (input.Kind != IdentifyKind.Cover)
            {
                return ScanningErrors.Invalid(
                    "scan.bound_cover_kind", "A read bound to a scanned item is a cover read: send kind cover.", "kind");
            }

            if (input.Items.Count != 1)
            {
                return ScanningErrors.Invalid(
                    "scan.bound_single_item", "A cover bound to a scanned item carries exactly one item.", "items");
            }
        }

        if (input.BoundItemId is not null)
        {
            if (input.Kind != IdentifyKind.EditionProof)
            {
                return ScanningErrors.Invalid(
                    "scan.bound_proof_kind", "A read bound to a library item is an edition proof: send kind edition_proof.", "kind");
            }

            if (input.Items.Count != 1)
            {
                return ScanningErrors.Invalid(
                    "scan.proof_single_item", "An edition proof carries exactly one item.", "items");
            }
        }

        if (input is { BoundItemId: null, Kind: IdentifyKind.EditionProof })
        {
            return ScanningErrors.Invalid(
                "scan.proof_item_required", "An edition proof is bound to a library item: send its itemId.", "itemId");
        }

        for (var i = 0; i < input.Items.Count; i++)
        {
            if (input.Items[i] is not { } item)
            {
                return ScanningErrors.Invalid("scan.item_invalid", "An item was empty.", $"items[{i}]");
            }

            if (SizeOf(item) > MaxItemBytes)
            {
                return ScanningErrors.Invalid("scan.item_too_large", $"An item can be at most {MaxItemBytes / 1024} KB of text.", $"items[{i}]");
            }

            if (!string.IsNullOrWhiteSpace(item.ProductCode) && !ProductCode.IsAcceptable(item.ProductCode))
            {
                return ScanningErrors.Invalid(
                    "scan.product_code_invalid",
                    $"A product code is at most {ProductCode.MaxInputLength} characters, shaped like SNS-ADNE-USA or SLUS-00594.",
                    $"items[{i}].productCode");
            }
        }

        return null;
    }

    private static int SizeOf(IdentifyItemInput item) =>
        new[] { item.LocalId, item.SegmentId, item.Text, item.Title, item.Creator, item.Edition, item.Platform, item.ProductCode }
            .Sum(value => value is null ? 0 : Encoding.UTF8.GetByteCount(value));

    /// <summary>
    /// Cleans the items of one snap, and tells each read that names no kind what kind the rest of the snap agreed on
    /// (<see cref="KindContext"/>, which only orders level-scoring releases). The eval grounder cleans through here too, so the
    /// live path and the eval run order a shelf's kindless reads by one rule.
    /// </summary>
    public static List<CleanItem> Clean(IReadOnlyList<IdentifyItemInput> items, IdentifyKind kind = IdentifyKind.Shelf, IGameSerialLookup? serials = null) =>
        WithSnapKind(CleanEach(items, kind, serials));

    private static List<CleanItem> WithSnapKind(List<CleanItem> cleaned) =>
        KindContext.Dominant(cleaned.Select(c => c.Facts.Kind)) is { } snapKind
            ? [.. cleaned.Select(c => c.Facts.Kind is null ? c with { Facts = c.Facts with { KindContext = snapKind } } : c)]
            : cleaned;

    private static List<CleanItem> CleanEach(IReadOnlyList<IdentifyItemInput> items, IdentifyKind kind, IGameSerialLookup? serials) =>
        items.Select(item =>
        {
            var read = new ReadFacts(
                Text(item.Title, MaxFieldLength),
                Text(item.Text, MaxTextLength),
                Text(item.Creator, MaxFieldLength),
                Text(item.Edition, MaxFieldLength),
                item.Completeness,
                item.Kind,
                item.Format,
                item.Year is >= MinYear and <= MaxYear ? item.Year : null,
                Text(item.Platform, MaxPlatformLength));

            // A product code that names exactly one game identifies the read (ADR-031); the title and platform it gives are the
            // table's, so they go in before the sanitiser, which then treats the read as the game it now is.
            if (serials is not null)
            {
                read = ProductCodeReads.Resolve(read, item.ProductCode, serials);
            }

            // What the model put in a field that cannot hold it is dropped before anything scores or stores it.
            var facts = ReadSanitizer.Sanitize(read);
            var profile = ScoringProfileSelector.Choose(facts.Completeness, facts.Kind, facts.Format, items.Count, kind is IdentifyKind.Cover or IdentifyKind.EditionProof);
            return new CleanItem(
                Text(item.LocalId, MaxLocalIdLength), Text(item.SegmentId, MaxSegmentIdLength), facts, item.LocalMatchReleaseId, profile, item.Box?.Clamped());
        }).ToList();

    /// <summary>Strips control and invisible formatting characters (a newline or tab becomes a space), trims, and cuts to length. Empty becomes null.</summary>
    public static string? Text(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (c is '\n' or '\r' or '\t')
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            {
                builder.Append(c);
            }

            if (builder.Length > maxLength)
            {
                break;
            }
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength];
            if (char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1];
            }
        }

        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>
/// What is kept of a read in the candidate's <c>inferred</c> column, so the card can show chips and the background job can ask
/// again without the phone. Camel-cased, enums as their API text, empty fields left out.
/// </summary>
internal sealed record StoredRead
{
    public string? Title { get; init; }

    public string? Creator { get; init; }

    public string? Edition { get; init; }

    public string? Completeness { get; init; }

    public string? Kind { get; init; }

    public string? Format { get; init; }

    public int? Year { get; init; }

    public string? Platform { get; init; }

    /// <summary>The product code that decided the title (ADR-031); with <see cref="ReadAs"/>, the words the read had before.</summary>
    public string? ProductCode { get; init; }

    public string? ReadAs { get; init; }

    public string? Profile { get; init; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ReadFacts facts, ScoringProfileKind profile) => JsonSerializer.Serialize(
        new StoredRead
        {
            Title = facts.Title,
            Creator = facts.Creator,
            Edition = facts.Edition,
            Completeness = facts.Completeness is { } completeness ? EnumText.ToText(completeness) : null,
            Kind = facts.Kind is { } kind ? EnumText.ToText(kind) : null,
            Format = facts.Format is { } format ? EnumText.ToText(format) : null,
            Year = facts.Year,
            Platform = facts.Platform,
            ProductCode = facts.ProductCode,
            ReadAs = facts.ReadAs,
            Profile = profile.ToString(),
        },
        Options);

    /// <summary>The read and the profile that were stored on a candidate; a candidate without them reads as just its text.</summary>
    public static (ReadFacts Facts, ScoringProfileKind Profile) From(ScanCandidate candidate)
    {
        var stored = ReadStored(candidate.InferredJson);
        var facts = new ReadFacts(
            stored?.Title,
            candidate.VisibleText,
            stored?.Creator,
            stored?.Edition,
            Parse<ItemCompleteness>(stored?.Completeness),
            Parse<WorkKind>(stored?.Kind),
            Parse<ReleaseFormat>(stored?.Format),
            stored?.Year,
            stored?.Platform,
            stored?.ProductCode,
            stored?.ReadAs);
        var profile = Enum.TryParse<ScoringProfileKind>(stored?.Profile, out var kind) ? kind : ScoringProfileKind.Cover;
        return (facts, profile);
    }

    /// <summary>The kind a stored read named, or null when it named none (or the read cannot be read).</summary>
    public static WorkKind? KindOf(string? inferredJson) => Parse<WorkKind>(ReadStored(inferredJson)?.Kind);

    private static StoredRead? ReadStored(string? inferredJson)
    {
        if (string.IsNullOrWhiteSpace(inferredJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredRead>(inferredJson, Options);
        }
        catch (JsonException)
        {
            return null; // Unreadable: fall back to the text alone.
        }
    }

    private static T? Parse<T>(string? text)
        where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            if (EnumText.ToText(value) == text)
            {
                return value;
            }
        }

        return null;
    }
}
