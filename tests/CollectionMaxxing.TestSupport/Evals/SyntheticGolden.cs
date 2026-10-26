using System.Text.Json;

namespace CollectionMaxxing.TestSupport.Evals;

/// <summary>
/// A golden set generated for a test, in a temp folder: tiny generated pictures (never a real photo) and truth files written
/// from the values the test gives. Disposing deletes the folders. The shape is the one in docs/09: <c>shelf/&lt;id&gt;.jpg</c> and
/// <c>single/&lt;id&gt;.jpg</c>, each with <c>&lt;id&gt;.truth.json</c>.
/// </summary>
public sealed class SyntheticGolden : IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _parent = Path.Combine(Path.GetTempPath(), "cm-evals-" + Guid.NewGuid().ToString("N"));

    public SyntheticGolden()
    {
        Golden = Path.Combine(_parent, "golden");
        Results = Path.Combine(_parent, "results");
        Directory.CreateDirectory(Path.Combine(Golden, "shelf"));
        Directory.CreateDirectory(Path.Combine(Golden, "single"));
        Directory.CreateDirectory(Results);
    }

    /// <summary>The golden folder (what <c>Evals:GoldenPath</c> points to).</summary>
    public string Golden { get; }

    /// <summary>The results folder (what <c>Evals:ResultsPath</c> points to).</summary>
    public string Results { get; }

    /// <summary>The folder both of them are in.</summary>
    public string Root => _parent;

    /// <summary>The bytes every generated picture has, so a test can look for them.</summary>
    public static byte[] PictureBytes { get; } = JpegFixtures.Plain();

    /// <summary>A shelf picture. <paramref name="truth"/> is the list of items on it; null writes no truth file.</summary>
    public string AddShelf(string id, IEnumerable<ShelfItem>? truth, int unreadable = 0, string extension = ".jpg", string? shotKind = null)
    {
        var photo = WritePicture("shelf", id, extension);
        if (truth is not null)
        {
            File.WriteAllText(
                Path.Combine(Golden, "shelf", id + ".truth.json"),
                JsonSerializer.Serialize(new { items = truth.ToList(), unreadable, shotKind }, Options));
        }

        return photo;
    }

    /// <summary>A single-item picture. <paramref name="truth"/> null writes no truth file.</summary>
    public string AddSingle(string id, SingleItem? truth, string extension = ".jpg")
    {
        var photo = WritePicture("single", id, extension);
        if (truth is not null)
        {
            File.WriteAllText(Path.Combine(Golden, "single", id + ".truth.json"), JsonSerializer.Serialize(truth, Options));
        }

        return photo;
    }

    /// <summary>Writes any file into a set's folder (for the "only pictures are served" tests).</summary>
    public string AddFile(string set, string fileName, string content)
    {
        var path = Path.Combine(Golden, set, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Every file under the given folder, relative to it, sorted: a test uses it to prove nothing was written.</summary>
    public static IReadOnlyList<string> Files(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order().ToList()
            : [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (IOException)
        {
            // A test host may still hold a file for a moment; the temp folder is cleaned up later.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private string WritePicture(string set, string id, string extension)
    {
        var path = Path.Combine(Golden, set, id + extension);
        File.WriteAllBytes(path, PictureBytes);
        return path;
    }

    /// <summary>One item on a shelf, as the truth file spells it.</summary>
    public sealed record ShelfItem(string Title, string MediaType, string? Platform = null, int? Year = null);

    /// <summary>The truth of a single-item picture, as the truth file spells it.</summary>
    public sealed record SingleItem(
        string Title, string Format, string Completeness, string Kind, string? Platform = null, string? Edition = null, string? ShotKind = null);
}
