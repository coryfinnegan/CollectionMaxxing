using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Evals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Evals;

/// <summary>
/// The phone's readings of the last scored run as <c>latest-inputs.json</c> in <c>Evals:ResultsPath</c>, next to <c>latest-run.json</c>, and the empty
/// <c>reground.request</c> file that asks for them to be grounded again. The readings hold the owner's real titles, like the rest of the results folder: PC
/// only, never served, never in the repo (<c>.gitignore</c> covers the folder). The file is written whole to a temp name and moved into place, so a stop
/// half way leaves the previous readings; a newer run replaces them. Only someone who can write the folder can ask for a re-ground.
/// </summary>
public sealed class FileEvalInputs(IOptions<EvalsOptions> options, ILogger<FileEvalInputs> logger, Action<string>? delete = null) : IEvalInputs
{
    private const string InputsFile = "latest-inputs.json";
    private const string RequestFile = "reground.request";
    private const int MaxFileBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DictionaryKeyPolicy = null,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Action<string> _delete = delete ?? File.Delete;
    private int _deleteFailureLogged;

    public bool IsConfigured => Folder() is not null;

    public async Task SaveAsync(KeptRunInputs inputs, CancellationToken cancellationToken)
    {
        var folder = Folder() ?? throw new InvalidOperationException("Evals:ResultsPath is not set.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, InputsFile);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(inputs, Json), cancellationToken);
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

    public KeptRunInputs? Load()
    {
        if (Folder() is not { } folder)
        {
            return null;
        }

        try
        {
            var path = Path.Combine(folder, InputsFile);
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileBytes ? JsonSerializer.Deserialize<KeptRunInputs>(File.ReadAllText(path), Json) : null;
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

    public bool HasRegroundRequest() => Folder() is { } folder && File.Exists(Path.Combine(folder, RequestFile));

    public bool TakeRegroundRequest()
    {
        if (Folder() is not { } folder)
        {
            return false;
        }

        try
        {
            var path = Path.Combine(folder, RequestFile);
            if (!File.Exists(path))
            {
                return false;
            }

            _delete(path); // deleted first: a file that cannot be removed must not start a re-ground on every look
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The worker looks every few seconds, so this says it once; the message may name a path, so only the type is logged.
            if (Interlocked.Exchange(ref _deleteFailureLogged, 1) == 0)
            {
                logger.LogWarning("The eval re-ground request file could not be deleted ({Failure}), so it is ignored until it can be.", exception.GetType().Name);
            }

            return false;
        }
    }

    private string? Folder() =>
        options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.ResultsPath) ? Path.GetFullPath(options.Value.ResultsPath) : null;
}
