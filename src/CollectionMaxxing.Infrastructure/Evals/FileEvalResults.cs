using System.Globalization;
using System.Text.Json;
using CollectionMaxxing.Application.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Evals;

/// <summary>
/// Run results as files in <c>Evals:ResultsPath</c> (the evals have no database tables): the dated aggregate file
/// (<c>yyyy-MM-dd-&lt;sha&gt;.json</c>, safe to commit), <c>latest.json</c> (the same), <c>latest-run.json</c> (the grounded run, with
/// titles: PC only) and <c>report.md</c> (PC only). Each file is written whole to a temp name and moved into place.
/// </summary>
public sealed class FileEvalResults(IOptions<EvalsOptions> options) : IEvalResults
{
    private const string LatestSummary = "latest.json";
    private const string LatestRun = "latest-run.json";
    private const string Report = "report.md";
    private const string DraftFolder = "drafts";
    private const int MaxFileBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DictionaryKeyPolicy = null,
        MaxDepth = 16,
    };

    public bool IsConfigured => Folder() is not null;

    public async Task SaveAsync(EvalSummary summary, GroundedRun run, string reportMarkdown, CancellationToken cancellationToken)
    {
        var folder = Folder() ?? throw new InvalidOperationException("Evals:ResultsPath is not set.");
        Directory.CreateDirectory(folder);
        var summaryJson = JsonSerializer.Serialize(summary, Json);

        await WriteAsync(UniqueDatedPath(folder, summary), summaryJson, cancellationToken);
        await WriteAsync(Path.Combine(folder, LatestRun), JsonSerializer.Serialize(run, Json), cancellationToken);
        await WriteAsync(Path.Combine(folder, Report), reportMarkdown, cancellationToken);
        await WriteAsync(Path.Combine(folder, LatestSummary), summaryJson, cancellationToken); // last: its presence says the run is complete
    }

    public EvalSummary? LoadLatestSummary() => Read<EvalSummary>(LatestSummary);

    public GroundedRun? LoadLatestRun() => Read<GroundedRun>(LatestRun);

    public IReadOnlyDictionary<string, double>? LoadBaseline()
    {
        var path = options.Value.BaselinePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, "partial", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True))
            {
                return null; // the summary of a cancelled run is not a baseline
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var baseline = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var property in metrics.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var value))
                {
                    baseline[property.Name] = value;
                }
            }

            return baseline;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool TryWriteDraft(string id, string json)
    {
        if (Folder() is not { } folder || !EvalIds.IsValid(id))
        {
            return false;
        }

        try
        {
            var drafts = Path.Combine(folder, DraftFolder);
            Directory.CreateDirectory(drafts);
            using var stream = new FileStream(Path.Combine(drafts, id + ".truth.draft.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? Folder() =>
        options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.ResultsPath) ? Path.GetFullPath(options.Value.ResultsPath) : null;

    private T? Read<T>(string name)
        where T : class
    {
        if (Folder() is not { } folder)
        {
            return null;
        }

        try
        {
            var path = Path.Combine(folder, name);
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileBytes ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The dated file's path; a second run on the same day with the same sha gets <c>-2</c>, <c>-3</c>… so no earlier result is lost.</summary>
    private static string UniqueDatedPath(string folder, EvalSummary summary)
    {
        var stem = string.Create(CultureInfo.InvariantCulture, $"{summary.Date}-{summary.BuildSha}");
        var path = Path.Combine(folder, stem + ".json");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}.json"));
        }

        return path;
    }

    private static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
