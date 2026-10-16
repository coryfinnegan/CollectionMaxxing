using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Recognition;

/// <summary>The scoring section of the config file: thresholds, and weights per profile (docs/02 section 6).</summary>
public sealed record RecognitionScoringSection(ScoringThresholds Thresholds, RecognitionScoringWeights Weights)
{
    public static RecognitionScoringSection From(ScoringOptions options) => new(
        options.Thresholds,
        new RecognitionScoringWeights(options.BarcodeProduct, options.ShelfSpine, options.Cover, options.LooseCartridge, options.BareLabel));

    public ScoringOptions ToOptions() => new()
    {
        Thresholds = Thresholds,
        BarcodeProduct = Weights.BarcodeProduct,
        ShelfSpine = Weights.ShelfSpine,
        Cover = Weights.Cover,
        LooseCartridge = Weights.LooseCartridge,
        BareLabel = Weights.BareLabel,
    };
}

/// <summary>The weights of each scoring profile, named as <see cref="ScoringProfileKind"/>.</summary>
public sealed record RecognitionScoringWeights(
    ScoringWeights BarcodeProduct,
    ScoringWeights ShelfSpine,
    ScoringWeights Cover,
    ScoringWeights LooseCartridge,
    ScoringWeights BareLabel);

/// <summary>The whole config file: the defaults file and an override file have this shape (the override may name any subset).</summary>
public sealed record RecognitionFile(
    RecognitionPrompt Prompt,
    RecognitionAutoSnap AutoSnap,
    RecognitionEngine Engine,
    RecognitionSplitting Splitting,
    RecognitionScoringSection Scoring);

/// <summary>
/// The outcome of combining the defaults with an override. Either <see cref="Config"/> and <see cref="Scoring"/> are both set and
/// <see cref="Problems"/> is empty, or both are null and the problems say why: a config is never partly applied.
/// </summary>
public sealed record RecognitionBuild(RecognitionConfig? Config, ScoringOptions? Scoring, IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0 && Config is not null && Scoring is not null;

    public static RecognitionBuild Failed(IReadOnlyList<string> problems) => new(null, null, problems);
}

/// <summary>
/// Merges an override file over the defaults and validates the result. Pure: no files, no logging, no clock. The override is
/// <em>data</em>: unknown keys, wrong types and values out of range all fail it as a whole, and no message repeats the file's text.
/// </summary>
public static class RecognitionConfigBuilder
{
    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonDocumentOptions HandWritten = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    /// <param name="defaultsJson">The checked-in defaults (<see cref="RecognitionDefaults.Json"/>); a bug if they are invalid.</param>
    /// <param name="overrideJson">The override file's text, or null for none.</param>
    public static RecognitionBuild Build(string defaultsJson, string? overrideJson)
    {
        var merged = ParseObject(defaultsJson, "the defaults", out var defaultsProblem)
            ?? throw new InvalidOperationException("The checked-in recognition defaults are not a JSON object: " + defaultsProblem);
        merged["scoring"] = JsonSerializer.SerializeToNode(RecognitionScoringSection.From(new ScoringOptions()), Strict);

        if (overrideJson is not null)
        {
            var overrides = ParseObject(overrideJson, "The override file", out var parseProblem);
            if (overrides is null)
            {
                return RecognitionBuild.Failed([parseProblem!]);
            }

            var nulls = new List<string>();
            Merge(merged, overrides, string.Empty, nulls);
            if (nulls.Count > 0)
            {
                return RecognitionBuild.Failed(nulls);
            }
        }

        RecognitionFile file;
        try
        {
            file = merged.Deserialize<RecognitionFile>(Strict)
                ?? throw new JsonException("empty");
        }
        catch (JsonException e)
        {
            // The exception's message can quote the file; its path names only keys.
            var where = string.IsNullOrEmpty(e.Path) ? "the file" : e.Path.TrimStart('$', '.');
            return RecognitionBuild.Failed([$"{where} is not a setting this version knows, or has the wrong type (whole numbers where a whole number is needed, text where text is)."]);
        }

        var scoring = file.Scoring.ToOptions();
        var unversioned = new RecognitionConfig(string.Empty, file.Prompt, file.AutoSnap, file.Engine, file.Splitting);
        var problems = RecognitionConfigRules.Check(unversioned);
        problems.AddRange(RecognitionConfigRules.CheckScoring(scoring));
        if (problems.Count > 0)
        {
            return RecognitionBuild.Failed(problems);
        }

        return new RecognitionBuild(unversioned with { Version = VersionOf(unversioned) }, scoring, []);
    }

    /// <summary>A hash of the served content (everything but the version itself), so the version changes whenever the content does.</summary>
    public static string VersionOf(RecognitionConfig config)
    {
        var content = new { config.Prompt, config.AutoSnap, config.Engine, config.Splitting };
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, Strict));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private static JsonObject? ParseObject(string json, string what, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            problem = $"{what} is empty.";
            return null;
        }

        try
        {
            if (JsonNode.Parse(json, documentOptions: HandWritten) is JsonObject root)
            {
                return root;
            }

            problem = $"{what} must be a JSON object.";
        }
        catch (JsonException e)
        {
            var line = (e.LineNumber ?? 0) + 1;
            var column = (e.BytePositionInLine ?? 0) + 1;
            problem = string.Create(CultureInfo.InvariantCulture, $"{what} is not valid JSON (near line {line}, column {column}).");
        }

        return null;
    }

    /// <summary>Lays <paramref name="overrides"/> over <paramref name="target"/>: objects merge key by key, anything else replaces.</summary>
    private static void Merge(JsonObject target, JsonObject overrides, string path, List<string> nulls)
    {
        foreach (var (key, value) in overrides.ToList())
        {
            var here = path.Length == 0 ? key : path + "." + key;
            if (value is null)
            {
                nulls.Add($"{here} must have a value; null is not allowed.");
            }
            else if (value is JsonObject over && target[key] is JsonObject under)
            {
                Merge(under, over, here, nulls);
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }
}
