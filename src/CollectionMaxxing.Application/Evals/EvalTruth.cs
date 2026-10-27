using System.Text.Json;
using System.Text.Json.Nodes;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;

#pragma warning disable CA1720 // "Single" is the set's name in the golden folder, not the System.Single type.
namespace CollectionMaxxing.Application.Evals;

/// <summary>One picture in the golden folder, as the list endpoint shows it.</summary>
public sealed record GoldenPhotoDto(string Id, string Set, bool HasTruth, long Bytes);

/// <summary>One item the owner says is on a shelf picture.</summary>
public sealed record ShelfTruthItem(string Title, string MediaType, string? Platform, int? Year);

/// <summary>The truth of a shelf picture (docs/09): the readable items in order, and how many are visible but unreadable.</summary>
public sealed record ShelfTruth(IReadOnlyList<ShelfTruthItem> Items, int Unreadable);

/// <summary>The truth of a single-item picture (docs/09). <c>Kind</c> is one of <see cref="EvalKinds"/>.</summary>
public sealed record SingleTruth(string Title, string? Platform, string Format, string Completeness, string? Edition, string Kind);

/// <summary>The truth for one picture: exactly one of <see cref="Shelf"/> and <see cref="Single"/> is set, matching <see cref="Set"/>. <see cref="ShotKind"/> is the optional label of what the shot is, for slicing; null when the truth file does not label it.</summary>
public sealed record TruthEntry(string PhotoId, string Set, ShelfTruth? Shelf, SingleTruth? Single, string? ShotKind);

/// <summary>The two sets of the golden folder (the names of its sub-folders).</summary>
public static class GoldenSets
{
    public const string Shelf = "shelf";
    public const string Single = "single";

    public static readonly IReadOnlyList<string> All = [Shelf, Single];
}

/// <summary>
/// The item kinds the accuracy targets are set for (docs/02 section 5), and the targets. A single-item picture's truth names its
/// kind; shelf items are grouped by their media type instead.
/// </summary>
public static class EvalKinds
{
    public const string BookSpine = "book_spine";
    public const string GameBox = "game_box";
    public const string DiscCover = "disc_cover";
    public const string VhsSleeve = "vhs_sleeve";
    public const string LooseCartridge = "loose_cartridge";
    public const string BareLabel = "bare_label";

    /// <summary>Top-1 and top-3 targets in percent, per kind (docs/02: "Accuracy targets").</summary>
    public static readonly IReadOnlyDictionary<string, (double Top1, double Top3)> Targets = new Dictionary<string, (double, double)>
    {
        [BookSpine] = (88, 95),
        [GameBox] = (88, 96),
        [DiscCover] = (88, 96),
        [VhsSleeve] = (82, 93),
        [LooseCartridge] = (75, 90),
        [BareLabel] = (60, 82),
    };

    /// <summary>
    /// A key that is safe to put in a result file: lower snake case, at most 32 characters. Anything else (a typo that would be
    /// the owner's own words) becomes <c>other</c>, so a title can never end up in a committed file as a group name.
    /// </summary>
    public static string Key(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return "other";
        }

        var key = kind.Trim().ToLowerInvariant();
        return key.Length <= 32 && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') ? key : "other";
    }

    /// <summary>A first guess at a single picture's kind from what was read, for a draft truth file the owner then corrects.</summary>
    public static string Guess(ReleaseFormat? format, ItemCompleteness? completeness) => (format, completeness) switch
    {
        (_, ItemCompleteness.DiscOnly) => BareLabel,
        (ReleaseFormat.Vhs or ReleaseFormat.Betamax, ItemCompleteness.CartOnly) => BareLabel,
        (_, ItemCompleteness.CartOnly) => LooseCartridge,
        (ReleaseFormat.GameCartridge or ReleaseFormat.GameCard, _) => LooseCartridge,
        (ReleaseFormat.Vhs or ReleaseFormat.Betamax, _) => VhsSleeve,
        (ReleaseFormat.Hardcover or ReleaseFormat.Paperback or ReleaseFormat.MassMarket or ReleaseFormat.BoardBook or ReleaseFormat.SpiralBound, _) => BookSpine,
        (ReleaseFormat.Dvd or ReleaseFormat.Bluray or ReleaseFormat.UhdBluray or ReleaseFormat.Laserdisc, _) => DiscCover,
        _ => GameBox,
    };
}

/// <summary>Reads and writes truth files. Reading is forgiving about spelling (<c>mediaType</c> or <c>media_type</c>) and strict about what must be there.</summary>
public static class TruthFiles
{
    public const int MaxFileBytes = 256 * 1024;
    private const int MaxItems = 200;
    private const int MaxText = 500;

    private static readonly JsonDocumentOptions Parsing = new() { MaxDepth = 8, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static readonly JsonSerializerOptions Writing = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The truth in <paramref name="json"/> for a picture of the given set, or null when the file is not valid truth (so the picture is skipped, never half-scored).</summary>
    public static TruthEntry? Parse(string photoId, string set, string json)
    {
        if (json.Length > MaxFileBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, Parsing);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return set switch
            {
                GoldenSets.Shelf => ParseShelf(photoId, root),
                GoldenSets.Single => ParseSingle(photoId, root),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TruthEntry? ParseShelf(string photoId, JsonElement root)
    {
        if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array || itemsElement.GetArrayLength() > MaxItems)
        {
            return null;
        }

        var items = new List<ShelfTruthItem>();
        foreach (var element in itemsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || Text(element, "title") is not { } title)
            {
                return null;
            }

            items.Add(new ShelfTruthItem(title, Text(element, "mediaType", "media_type") ?? "other", Text(element, "platform"), Year(element)));
        }

        var unreadable = 0;
        if (root.TryGetProperty("unreadable", out var unreadableElement))
        {
            if (unreadableElement.ValueKind != JsonValueKind.Number || !unreadableElement.TryGetInt32(out unreadable) || unreadable < 0)
            {
                return null;
            }
        }

        return new TruthEntry(photoId, GoldenSets.Shelf, new ShelfTruth(items, unreadable), null, ShotKindOf(root));
    }

    private static TruthEntry? ParseSingle(string photoId, JsonElement root)
    {
        if (Text(root, "title") is not { } title)
        {
            return null;
        }

        var single = new SingleTruth(
            title,
            Text(root, "platform"),
            Text(root, "format") ?? "other",
            Text(root, "completeness") ?? "n_a",
            Text(root, "edition"),
            EvalKinds.Key(Text(root, "kind")));
        return new TruthEntry(photoId, GoldenSets.Single, null, single, ShotKindOf(root));
    }

    /// <summary>The picture's labelled shot kind, safe to put in a result file; null when the truth file does not label it. Any other key in the file is ignored, so old truth files keep parsing.</summary>
    private static string? ShotKindOf(JsonElement root)
    {
        var text = Text(root, "shotKind", "shot_kind");
        return text is null ? null : EvalKinds.Key(text);
    }

    private static string? Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text.Length > MaxText ? text[..MaxText] : text;
                }
            }
        }

        return null;
    }

    private static int? Year(JsonElement element) =>
        element.TryGetProperty("year", out var year) && year.ValueKind == JsonValueKind.Number && year.TryGetInt32(out var value) ? value : null;

    /// <summary>The text of a draft shelf truth file, marked as a draft so the owner knows to check it.</summary>
    public static string WriteShelfDraft(IEnumerable<ShelfTruthItem> items, int unreadable) => Write(new JsonObject
    {
        ["draft"] = true,
        ["note"] = "Written from a phone reading. Check every line, remove this note and the draft flag, then rename the file to <id>.truth.json.",
        ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["title"] = i.Title,
            ["mediaType"] = i.MediaType,
            ["platform"] = i.Platform,
            ["year"] = i.Year,
        }).ToArray()),
        ["unreadable"] = unreadable,
    });

    /// <summary>The text of a draft single-item truth file.</summary>
    public static string WriteSingleDraft(SingleTruth truth) => Write(new JsonObject
    {
        ["draft"] = true,
        ["note"] = "Written from a phone reading. Check every line, remove this note and the draft flag, then rename the file to <id>.truth.json.",
        ["title"] = truth.Title,
        ["platform"] = truth.Platform,
        ["format"] = truth.Format,
        ["completeness"] = truth.Completeness,
        ["edition"] = truth.Edition,
        ["kind"] = truth.Kind,
    });

    private static string Write(JsonObject node) => node.ToJsonString(Writing);

    /// <summary>The spelling of a work kind in a truth file (<c>game</c>, <c>film</c>…).</summary>
    public static string MediaTypeOf(WorkKind? kind) => kind is { } k ? EnumText.ToText(k) : "other";
}
