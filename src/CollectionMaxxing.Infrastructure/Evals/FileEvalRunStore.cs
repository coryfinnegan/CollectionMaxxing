using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Evals;

/// <summary>
/// The PC's one run as files under <c>Evals:ResultsPath/open-run</c>: <c>run.json</c> (the header), <c>photos/&lt;id&gt;.json</c> (a photo as the
/// phone sent it, after its checks) and <c>grounded/&lt;id&gt;.json</c> (what grounding made of it). A photo with a file in <c>photos</c> and none in
/// <c>grounded</c> is waiting to be grounded: the folder is the queue, so a restart loses nothing. It holds the owner's real titles, like the rest of
/// the results folder: PC only, never served, never in the repo. Every file is written whole to a temp name and moved into place.
/// </summary>
public sealed class FileEvalRunStore(IOptions<EvalsOptions> options) : IEvalRunStore
{
    private const string RunFolder = "open-run";
    private const string HeaderFile = "run.json";
    private const string PhotosFolder = "photos";
    private const string GroundedFolder = "grounded";
    private const int MaxFileBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DictionaryKeyPolicy = null,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };

    public bool IsConfigured => Root() is not null;

    public OpenRunHeader? Load() => Folder() is { } folder ? Read<OpenRunHeader>(Path.Combine(folder, HeaderFile)) : null;

    public void Create(OpenRunHeader header)
    {
        Discard();
        Directory.CreateDirectory(RequiredFolder());
        Save(header);
    }

    public void Save(OpenRunHeader header) => Write(Path.Combine(RequiredFolder(), HeaderFile), header);

    public void Discard()
    {
        if (Folder() is { } folder && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public IReadOnlySet<string> StoredIds() => Folder() is { } folder ? Ids(Path.Combine(folder, PhotosFolder)) : new HashSet<string>();

    public IReadOnlySet<string> GroundedIds() => Folder() is { } folder ? Ids(Path.Combine(folder, GroundedFolder)) : new HashSet<string>();

    public StoredEvalPhoto? LoadPhoto(string photoId) => Folder() is null ? null : Read<StoredEvalPhoto>(PhotoPath(PhotosFolder, photoId));

    public void SavePhoto(string photoId, StoredEvalPhoto photo) => Write(PhotoPath(PhotosFolder, photoId), photo);

    public void DeletePhoto(string photoId) => Delete(PhotoPath(PhotosFolder, photoId));

    public GroundedPhotoFile? LoadGrounded(string photoId) => Folder() is null ? null : Read<GroundedPhotoFile>(PhotoPath(GroundedFolder, photoId));

    public void SaveGrounded(string photoId, GroundedPhotoFile grounded) => Write(PhotoPath(GroundedFolder, photoId), grounded);

    public void DeleteGrounded(string photoId) => Delete(PhotoPath(GroundedFolder, photoId));

    public void ClearPhotos()
    {
        foreach (var name in new[] { PhotosFolder, GroundedFolder })
        {
            var folder = Path.Combine(RequiredFolder(), name);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private string? Root() =>
        options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.ResultsPath) ? Path.GetFullPath(options.Value.ResultsPath) : null;

    private string? Folder() => Root() is { } root ? Path.Combine(root, RunFolder) : null;

    private string RequiredFolder() => Folder() ?? throw new InvalidOperationException("Evals:ResultsPath is not set.");

    /// <summary>The path of a photo's file. The id was checked before it got here; checking it again keeps a path from ever being built out of anything else.</summary>
    private string PhotoPath(string subfolder, string photoId) =>
        EvalIds.IsValid(photoId)
            ? Path.Combine(RequiredFolder(), subfolder, photoId + ".json")
            : throw new ArgumentException("Not a photo id.", nameof(photoId));

    private static HashSet<string> Ids(string folder)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(folder))
        {
            return ids;
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            // A file being written has a temp name that does not end in .json, so it is not counted until it is moved into place.
            if (string.Equals(Path.GetExtension(file), ".json", StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(file) is { } id && EvalIds.IsValid(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static T? Read<T>(string path)
        where T : class
    {
        try
        {
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

    private static void Write<T>(string path, T content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(content, Json));
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

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
