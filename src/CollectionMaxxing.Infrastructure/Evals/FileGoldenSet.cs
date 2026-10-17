using CollectionMaxxing.Application.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Evals;

/// <summary>
/// The golden folder on the PC: <c>shelf/&lt;id&gt;.jpg|jpeg|heic</c> and <c>single/&lt;id&gt;.jpg|jpeg|heic</c>, each with
/// <c>&lt;id&gt;.truth.json</c>. The folder is listed afresh on every call, and a request's id is only ever looked up in that
/// listing: a request never builds a path. A file counts only when its name (without the extension) is a valid id and its
/// extension is a picture's; a link, or anything that resolves outside the folder, is ignored.
/// </summary>
public sealed class FileGoldenSet(IOptions<EvalsOptions> options) : IGoldenSet
{
    private const string TruthSuffix = ".truth.json";
    private const string DraftSuffix = ".truth.draft.json";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".heic"] = "image/heic",
    };

    private sealed record Entry(string Id, string Set, string Folder, string Path, string ContentType, long Bytes);

    public bool IsAvailable => Root() is not null;

    public IReadOnlyList<GoldenPhotoDto> List() =>
        [.. Scan().Select(e => new GoldenPhotoDto(e.Id, e.Set, HasValidTruth(e), e.Bytes)).OrderBy(p => p.Set, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)];

    public GoldenImage? OpenImage(string id)
    {
        if (Find(id) is not { } entry)
        {
            return null;
        }

        try
        {
            var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new GoldenImage(stream, entry.ContentType, stream.Length);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? SetOf(string id) => Find(id)?.Set;

    public string? ReadTruth(string id) => Find(id) is { } entry ? ReadTruthText(entry) : null;

    public bool HasDraft(string id) => Find(id) is { } entry && File.Exists(Path.Combine(entry.Folder, entry.Id + DraftSuffix));

    public bool TryWriteDraft(string id, string json)
    {
        if (Find(id) is not { } entry)
        {
            return false;
        }

        try
        {
            // CreateNew: never replaces a file, so a draft the owner is editing is safe even if two runs race.
            using var stream = new FileStream(Path.Combine(entry.Folder, entry.Id + DraftSuffix), FileMode.CreateNew, FileAccess.Write, FileShare.None);
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

    private string? Root()
    {
        var settings = options.Value;
        return settings.Enabled && !string.IsNullOrWhiteSpace(settings.GoldenPath) && Directory.Exists(settings.GoldenPath)
            ? Path.GetFullPath(settings.GoldenPath)
            : null;
    }

    private Entry? Find(string id)
    {
        if (!EvalIds.IsValid(id))
        {
            return null;
        }

        return Scan().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
    }

    private static string? ReadTruthText(Entry entry)
    {
        try
        {
            var path = Path.Combine(entry.Folder, entry.Id + TruthSuffix);
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > TruthFiles.MaxFileBytes)
            {
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasValidTruth(Entry entry) =>
        ReadTruthText(entry) is { } text && TruthFiles.Parse(entry.Id, entry.Set, text) is not null;

    /// <summary>The pictures in the folder. An id found twice (say <c>a.jpg</c> and <c>a.jpeg</c>, or in both sets) is left out entirely.</summary>
    private List<Entry> Scan()
    {
        if (Root() is not { } root)
        {
            return [];
        }

        var found = new List<Entry>();
        foreach (var set in GoldenSets.All)
        {
            var folder = Path.Combine(root, set);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    if (AsEntry(root, set, folder, path) is { } entry)
                    {
                        found.Add(entry);
                    }
                }
            }
            catch (IOException)
            {
                // A folder that cannot be listed has no pictures this time.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }

        return [.. found.GroupBy(e => e.Id, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single())];
    }

    private static Entry? AsEntry(string root, string set, string folder, string path)
    {
        if (!ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
        {
            return null;
        }

        var id = Path.GetFileNameWithoutExtension(path);
        if (!EvalIds.IsValid(id))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.Directory))
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, comparison) ? new Entry(id, set, folder, full, contentType, info.Length) : null;
    }
}
