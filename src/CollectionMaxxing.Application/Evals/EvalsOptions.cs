using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The eval server's settings (<c>Evals</c> section). The routes exist only when <see cref="Enabled"/> is true and
/// <see cref="GoldenPath"/> is a folder; otherwise every one of them is a 404. The folders are on the owner's PC, outside the repo.
/// </summary>
public sealed class EvalsOptions
{
    public const string SectionName = "Evals";

    /// <summary>The most a run upload may weigh (docs/09 and the brief: 2 MB).</summary>
    public const int MaxRunBytes = 2 * 1024 * 1024;

    public bool Enabled { get; set; }

    /// <summary>The golden folder: <c>shelf/</c> and <c>single/</c>, pictures with a truth file each. Read; drafts are written beside the pictures when it is writable.</summary>
    public string? GoldenPath { get; set; }

    /// <summary>Where run results are written (<c>&lt;date&gt;-&lt;sha&gt;.json</c>, <c>latest.json</c>, <c>report.md</c>). Writable. Never served.</summary>
    public string? ResultsPath { get; set; }

    /// <summary>The baseline the regression check compares to (<c>evals/baseline.json</c> in the repo). Optional; with none, the check says so.</summary>
    public string? BaselinePath { get; set; }

    /// <summary>The git sha to put in the result file's name. Optional: the <c>CM_TAG</c> environment variable and then the build's own version are tried.</summary>
    public string? BuildSha { get; set; }
}

/// <summary>The rule for a photo id. An id comes from a file name and is the only thing a request may use to name a file.</summary>
public static class EvalIds
{
    public const int MaxLength = 64;

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

public static class EvalErrors
{
    /// <summary>The same answer for "evals are off", "no golden folder" and "no such picture": nothing says which.</summary>
    public static readonly DomainError NotFound = new(ErrorKind.NotFound, "evals.not_found", "Not found.");

    public static readonly DomainError NoRunYet = new(ErrorKind.NotFound, "evals.no_run_yet", "No eval run has been uploaded yet.");

    public static readonly DomainError ResultsPathMissing = new(
        ErrorKind.Conflict, "evals.results_path_missing", "Set Evals:ResultsPath to a writable folder before uploading a run.");

    public static readonly DomainError ResultsNotWritable = new(
        ErrorKind.Conflict, "evals.results_not_writable", "The results folder could not be written. Check that it exists and is writable.");

    public static DomainError Invalid(string code, string message, string field) => new(ErrorKind.Validation, code, message, field);
}
