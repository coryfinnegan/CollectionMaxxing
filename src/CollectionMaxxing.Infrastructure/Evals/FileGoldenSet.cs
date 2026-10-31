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

    // One add at a time: the check that an id is free in both sets and the creating of its file must not interleave with another add.
    private readonly object _addLock = new();

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

    public GoldenAddResult Add(string goldenSet, string id, GoldenPhotoFormat format, ReadOnlyMemory<byte> content)
    {
        if (Root() is not { } root)
        {
            return new GoldenAddResult(GoldenAddOutcome.Unavailable);
        }

        // The path is built from a set that is one of the two names, an id that is plain, and an extension that is ours: nothing else.
        if (!GoldenSets.All.Contains(goldenSet, StringComparer.Ordinal) || !EvalIds.IsValid(id) || GoldenPhotoNames.IsReserved(id))
        {
            return new GoldenAddResult(GoldenAddOutcome.NotWritable);
        }

        lock (_addLock)
        {
            try
            {
                var folder = Path.Combine(root, goldenSet);
                if (!EnsurePlainFolder(root, folder))
                {
                    return new GoldenAddResult(GoldenAddOutcome.NotWritable);
                }

                if (IsTaken(root, id))
                {
                    return new GoldenAddResult(GoldenAddOutcome.IdTaken);
                }

                var path = Path.GetFullPath(Path.Combine(folder, id + GoldenPhotoFormats.Extension(format)));
                return IsInside(root, path) ? Create(path, id, goldenSet, content) : new GoldenAddResult(GoldenAddOutcome.NotWritable);
            }
            catch (IOException)
            {
                return new GoldenAddResult(GoldenAddOutcome.NotWritable);
            }
            catch (UnauthorizedAccessException)
            {
                return new GoldenAddResult(GoldenAddOutcome.NotWritable);
            }
        }
    }

    /// <summary>Creates the file (<c>CreateNew</c>: never over an existing one) and writes all of it, or leaves nothing.</summary>
    private static GoldenAddResult Create(string path, string id, string set, ReadOnlyMemory<byte> content)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
        }
        catch (IOException)
        {
            // Created by someone else since the check, or the name is not usable: the folder is not changed.
            return new GoldenAddResult(File.Exists(path) ? GoldenAddOutcome.IdTaken : GoldenAddOutcome.NotWritable);
        }
        catch (UnauthorizedAccessException)
        {
            return new GoldenAddResult(GoldenAddOutcome.NotWritable);
        }

        try
        {
            using (stream)
            {
                stream.Write(content.Span);
                stream.Flush(flushToDisk: true);
            }

            return new GoldenAddResult(GoldenAddOutcome.Added, new GoldenPhotoDto(id, set, false, content.Length));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(path); // the file is ours (CreateNew made it a moment ago): a half-written picture must not be listed
            return new GoldenAddResult(GoldenAddOutcome.NotWritable);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing more can be done for a file that will not go.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>
    /// The set folder, made when nothing is at that name (a fixed name, directly inside the golden folder). True only when it is then a plain
    /// folder inside the golden folder: a file, or a link (a dangling one too), at that name is never made into a folder or written through.
    /// </summary>
    private static bool EnsurePlainFolder(string root, string folder)
    {
        if (!Path.Exists(folder) && new DirectoryInfo(folder).LinkTarget is null)
        {
            Directory.CreateDirectory(folder);
        }

        return IsPlainFolderInside(root, folder);
    }

    /// <summary>
    /// True when anything in either set folder is named <c>&lt;id&gt;.…</c>, in any letter case (the PC's file system may not tell the cases apart):
    /// a picture of any extension, a truth file or draft left behind, even a folder. A set folder that is a link is not looked into, as the listing does not.
    /// </summary>
    private static bool IsTaken(string root, string id)
    {
        var prefix = id + ".";
        foreach (var set in GoldenSets.All)
        {
            var folder = Path.Combine(root, set);
            if (IsPlainFolderInside(root, folder)
                && Directory.EnumerateFileSystemEntries(folder).Any(entry => Path.GetFileName(entry).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInside(string root, string full)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, comparison);
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
            if (!IsPlainFolderInside(root, folder))
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

    /// <summary>A set folder counts only when it exists, is not a link (a junction or symbolic link) and sits inside the golden root.</summary>
    private static bool IsPlainFolderInside(string root, string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            {
                return false;
            }

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return Path.GetFullPath(folder).StartsWith(rootWithSeparator, comparison);
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
