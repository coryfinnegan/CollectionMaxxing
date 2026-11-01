namespace CollectionMaxxing.Application.Evals;

/// <summary>One golden photo of a scored run, as the phone sent it after the PC's checks: the engine results that passed.</summary>
public sealed record KeptPhoto(string PhotoId, IReadOnlyList<EvalPhotoInput> Results);

/// <summary>
/// What the PC keeps of the last scored phone run so a later server version can ground and score it again without the phone
/// (<c>latest-inputs.json</c> in <c>Evals:ResultsPath</c>): the phone's raw readings per photo, the notes the checks made while the run was taken in
/// (<c>Skipped</c>: refused photos, trimmed items, and so on, not the skips the scoring itself adds), and the run's own facts. <c>ReadAt</c> is when the
/// phone read it, <c>OpenedBy</c> the person whose libraries the grounding acts for. It holds the owner's real titles, like the rest of the results
/// folder: PC only, never served, never in the repo.
/// </summary>
public sealed record KeptRunInputs(
    string RunId,
    DateTimeOffset ReadAt,
    EvalDevice Device,
    string ConfigVersion,
    string? BaselineNote,
    bool Partial,
    Guid? OpenedBy,
    IReadOnlyList<KeptPhoto> Photos,
    IReadOnlyList<SkippedPhoto> Skipped);

/// <summary>
/// The phone's readings of the last run, kept beside the result files, and the one-file switch that asks for them to be grounded again now. Only
/// someone who can write the results folder can use either. Reads never throw for a missing or unreadable file.
/// </summary>
public interface IEvalInputs
{
    /// <summary>True when <c>Evals:ResultsPath</c> is set and the evals are on.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Keeps these readings, replacing the ones kept before: the whole file is written to a temp name and moved into place. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the folder cannot be written.
    /// </summary>
    Task SaveAsync(KeptRunInputs inputs, CancellationToken cancellationToken);

    /// <summary>The kept readings, or null when there are none (or the file cannot be read).</summary>
    KeptRunInputs? Load();

    /// <summary>True when <c>reground.request</c> is in the results folder. Only looks: the file stays.</summary>
    bool HasRegroundRequest();

    /// <summary>
    /// True when <c>reground.request</c> was in the results folder; the file is deleted as it is taken. When it cannot be deleted this says false, so a
    /// file that stays never starts a re-ground over and over.
    /// </summary>
    bool TakeRegroundRequest();
}
