namespace CollectionMaxxing.Application.Evals;

/// <summary>Where the PC's one eval run is: collecting photos from the phone, waiting to be scored, or scored.</summary>
public enum OpenRunState
{
    /// <summary>The phone is sending photos. Nothing is scored.</summary>
    Open,

    /// <summary>The phone asked to finish. The PC is grounding what is left and will score once.</summary>
    Finishing,

    /// <summary>Scored: the result files are written and the stored photos are gone.</summary>
    Done,
}

/// <summary>One golden photo of a run: its id and the set it is in (the phone reads a single like a cover and a shelf like a shelf).</summary>
public sealed record EvalRunPhoto(string Id, string Set);

/// <summary>
/// What the PC keeps of an open run besides its photos (<c>run.json</c>): who opened it, the golden photos it covers in reading order, and where
/// it is. <c>Refused</c> lists the photos the PC refused alone (with the problem code), so the phone does not read them again and the score lists
/// them as skipped. <c>Failure</c> is the last problem scoring hit (for example a results folder that could not be written); the PC tries again.
/// </summary>
public sealed record OpenRunHeader(
    string RunId,
    DateTimeOffset OpenedAt,
    EvalDevice Device,
    string ConfigVersion,
    string? BaselineNote,
    IReadOnlyList<EvalRunPhoto> Photos,
    OpenRunState State = OpenRunState.Open,
    bool Partial = false,
    DateTimeOffset? FinishedAt = null,
    IReadOnlyList<SkippedPhoto>? Refused = null,
    string? Failure = null);

/// <summary>
/// One photo as the phone sent it, after its checks: the engine results that passed and, for each that did not, why. Kept until the PC has grounded
/// it. Sending the photo again replaces it.
/// </summary>
public sealed record StoredEvalPhoto(IReadOnlyList<EvalPhotoInput> Results, IReadOnlyList<SkippedPhoto> Skipped, DateTimeOffset StoredAt);

/// <summary>What grounding made of a stored photo: one <see cref="GroundedPhoto"/> for each engine result, and what was skipped on the way.</summary>
public sealed record GroundedPhotoFile(IReadOnlyList<GroundedPhoto> Photos, IReadOnlyList<SkippedPhoto> Skipped);

/// <summary>The input to open a run: the same device, config version and note the one-shot run carries.</summary>
public sealed record EvalOpenRunInput(EvalDeviceInput? Device, string? ConfigVersion, string? BaselineNote);

/// <summary>One photo the phone sends: the id from the route and its engine results.</summary>
public sealed record EvalPhotoSubmission(string? PhotoId, IReadOnlyList<EvalPhotoInput>? Results);

/// <summary>
/// The open run as the phone sees it: the golden photos in reading order, which of them the PC holds (<c>Received</c>, in run order), how many of
/// those are grounded, the photos it refused, and where the run is. When <c>State</c> is <c>done</c>, <c>Summary</c> is the scored run.
/// </summary>
public sealed record EvalOpenRunStatus(
    string RunId,
    string State,
    bool Partial,
    DateTimeOffset OpenedAt,
    string ConfigVersion,
    EvalDevice Device,
    string? BaselineNote,
    IReadOnlyList<EvalRunPhoto> Photos,
    IReadOnlyList<string> Received,
    int Grounded,
    IReadOnlyList<SkippedPhoto> Refused,
    string? Failure,
    EvalSummary? Summary);

/// <summary>The answer to a stored photo: how many photos the PC holds now, of how many the run covers.</summary>
public sealed record EvalPhotoStored(string PhotoId, int Received, int Total);
