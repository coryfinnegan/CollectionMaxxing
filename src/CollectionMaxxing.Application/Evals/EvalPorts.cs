namespace CollectionMaxxing.Application.Evals;

/// <summary>A picture ready to stream. The caller owns the stream.</summary>
public sealed record GoldenImage(Stream Content, string ContentType, long Length);

/// <summary>
/// The golden folder on the PC. A request never names a file: it names an id, and the id is looked up in the listing the folder
/// gives. Nothing outside the folder, and nothing that is not a picture, is ever opened for a request.
/// </summary>
public interface IGoldenSet
{
    /// <summary>True when the evals are enabled and the golden folder exists. When false every eval route is a 404.</summary>
    bool IsAvailable { get; }

    /// <summary>The pictures in the folder (both sets), by id. An id that appears twice is left out, so an id names one picture.</summary>
    IReadOnlyList<GoldenPhotoDto> List();

    /// <summary>The picture with that id, or null when the listing has none. Never throws for a bad id.</summary>
    GoldenImage? OpenImage(string id);

    /// <summary>The set a listed picture is in (<see cref="GoldenSets"/>), or null when the id is not listed.</summary>
    string? SetOf(string id);

    /// <summary>The text of <c>&lt;id&gt;.truth.json</c>, or null when the picture has none (or it is too big).</summary>
    string? ReadTruth(string id);

    /// <summary>True when a draft truth file already exists for the picture (it is never overwritten: the owner may be editing it).</summary>
    bool HasDraft(string id);

    /// <summary>Writes <c>&lt;id&gt;.truth.draft.json</c> beside the picture. False when the folder is read-only or the write failed.</summary>
    bool TryWriteDraft(string id, string json);
}

/// <summary>Where run results are kept on the PC: dated aggregate files, the latest run in full, and the report. Never served as files.</summary>
public interface IEvalResults
{
    /// <summary>True when <c>Evals:ResultsPath</c> is set.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Writes the run: <c>&lt;date&gt;-&lt;sha&gt;.json</c> (the summary, aggregates only), <c>latest.json</c> (the same),
    /// <c>latest-run.json</c> (the grounded run, with titles) and <c>report.md</c>. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the folder cannot be written; nothing is half written.
    /// </summary>
    Task SaveAsync(EvalSummary summary, GroundedRun run, string reportMarkdown, CancellationToken cancellationToken);

    /// <summary>The summary of the latest run, or null when there is none (or it cannot be read).</summary>
    EvalSummary? LoadLatestSummary();

    /// <summary>The grounded latest run (what <see cref="SaveAsync"/> kept), or null.</summary>
    GroundedRun? LoadLatestRun();

    /// <summary>The baseline metrics (<c>evals/baseline.json</c>), or null when there is no baseline.</summary>
    IReadOnlyDictionary<string, double>? LoadBaseline();

    /// <summary>Writes a draft truth file under the results folder, for when the golden folder is read-only. False when it could not.</summary>
    bool TryWriteDraft(string id, string json);
}
