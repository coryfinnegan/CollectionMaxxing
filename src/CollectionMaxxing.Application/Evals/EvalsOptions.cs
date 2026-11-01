using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The eval server's settings (<c>Evals</c> section). The routes exist only when <see cref="Enabled"/> is true and
/// <see cref="GoldenPath"/> is a folder; otherwise every one of them is a 404. The folders are on the owner's PC, outside the repo.
/// </summary>
public sealed class EvalsOptions
{
    public const string SectionName = "Evals";

    /// <summary>The most a run upload may weigh (2 MB; a run is JSON, with no picture in it).</summary>
    public const int MaxRunBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The most one golden photo upload may weigh: 40 MB. A phone's original is far bigger than the 2048 px copy a share sheet makes
    /// (a 2576 px scan is a few MB; a 48 MP HEIC or a ProRAW-sized JPEG is more), and this is the one route that takes a body this big.
    /// </summary>
    public const int MaxGoldenPhotoBytes = 40 * 1024 * 1024;

    public bool Enabled { get; set; }

    /// <summary>The golden folder: <c>shelf/</c> and <c>single/</c>, pictures with a truth file each. Read; drafts are written beside the pictures when it is writable.</summary>
    public string? GoldenPath { get; set; }

    /// <summary>Where run results are written (<c>&lt;date&gt;-&lt;sha&gt;.json</c>, <c>latest.json</c>, <c>report.md</c>). Writable. Never served.</summary>
    public string? ResultsPath { get; set; }

    /// <summary>The baseline the regression check compares to (<c>evals/baseline.json</c> in the repo). Optional; with none, the check says so.</summary>
    public string? BaselinePath { get; set; }

    /// <summary>The git sha to put in the result file's name. Optional: the <c>CM_BUILD_SHA</c> environment variable and then the build's own version are tried.</summary>
    public string? BuildSha { get; set; }

    /// <summary>
    /// How many of one photo's reads have their providers asked at the same time while grounding. The providers' own limiters set the
    /// pace (Open Library allows 3 requests a second with a contact email, 1 without), so more than a handful in flight only queues;
    /// each ask has the patient background-job budget times the number of asks in flight, counted from when it starts, because every
    /// call waits in the shared limiter behind the others. A value below 1 asks one read at a time.
    /// </summary>
    public int GroundingParallelism { get; set; } = 6;

    /// <summary>
    /// Whether the worker, when the API starts as another server version than the one that made the latest result, grounds and scores the kept phone
    /// readings of that run again by itself (<c>latest-inputs.json</c>), so a fix on the PC's side is measured without a phone run. On by default. This
    /// switches off only that automatic step: an empty <c>reground.request</c> file in <see cref="ResultsPath"/> still asks for one on purpose.
    /// </summary>
    public bool RegroundOnStart { get; set; } = true;
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

    public static readonly DomainError NoOpenRun = new(ErrorKind.NotFound, "evals.no_open_run", "There is no open eval run on the PC. Open one first.");

    public static readonly DomainError RunAlreadyOpen = new(
        ErrorKind.Conflict, "evals.run_open", "A run is already open on the PC. Continue it, or discard it to start over.");

    public static readonly DomainError RunNotOpen = new(
        ErrorKind.Conflict, "evals.run_not_open", "The open run has been finished, so it takes no more photos.");

    public static readonly DomainError NoPhotos = Invalid("evals.no_photos", "The golden folder has no photos to run.", "photos");

    public static readonly DomainError PhotoNotInRun = Invalid("evals.photo_not_in_run", "That photo is not part of the open run.", "photoId");

    public static readonly DomainError PhotoIdMismatch = Invalid(
        "evals.photo_id_mismatch", "Every result must name the photo in the address.", "results");

    public static readonly DomainError SetInvalid = Invalid("evals.set_invalid", "The set must be \"single\" or \"shelf\".", "set");

    public static readonly DomainError IdInvalid = Invalid(
        "evals.id_invalid",
        $"A photo's name may use letters, digits, \"-\" and \"_\", up to {EvalIds.MaxLength} characters, and cannot be a name Windows reserves (like con or nul).",
        "id");

    public static readonly DomainError IdTaken = new(
        ErrorKind.Conflict, "evals.id_taken", "That name is already used by another golden photo, in either set. Pick another.");

    public static readonly DomainError GoldenNotWritable = new(
        ErrorKind.Conflict, "evals.golden_not_writable", "The golden folder could not be written. Check that it is writable and that its single and shelf folders are plain folders.");

    public static DomainError Invalid(string code, string message, string field) => new(ErrorKind.Validation, code, message, field);
}
