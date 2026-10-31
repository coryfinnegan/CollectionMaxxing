using System.Reflection;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// Use case: the phone uploads what it read in the golden photos (<c>POST /api/admin/evals/runs</c>). Each read is grounded through
/// the same code as <c>/identify</c> in a dry run (nothing is persisted), scored against the truth files with the live scoring
/// options, and written as a result file and a report on the PC. A photo with no valid truth file is skipped and listed; for each
/// one a draft truth file is written (never overwriting one the owner may be editing), which is never used in scoring until renamed.
/// A run is refused only for what is wrong with the run (device, config version, no results, too many, a photo sent twice for one
/// engine). A photo that fails its own checks is skipped and listed with the problem code, and the run is scored without it: one
/// bad photo never throws away a run the phone cannot repeat (<see cref="EvalPhotoChecks"/>).
/// </summary>
public static class SubmitEvalRun
{
    public const int MaxPhotos = 400;
    private const int MaxText = 100;
    private const int MaxNote = 500;

    public sealed class Handler(
        IGoldenSet golden,
        IEvalResults results,
        EvalGrounder grounder,
        IIdGenerator ids,
        TimeProvider clock,
        IOptions<EvalsOptions> options,
        IOptionsMonitor<ScoringOptions> scoring,
        ILogger<Handler> logger)
    {
        public async Task<Result<EvalRunResult>> HandleAsync(EvalRunInput input, CancellationToken cancellationToken)
        {
            if (!golden.IsAvailable)
            {
                return EvalErrors.NotFound;
            }

            if (!results.IsConfigured)
            {
                return EvalErrors.ResultsPathMissing;
            }

            if (Validate(input) is { } invalid)
            {
                return invalid;
            }

            var skipped = new List<SkippedPhoto>();
            var (accepted, refusal) = Screen(input.Results!, skipped);
            if (refusal is not null)
            {
                return refusal;
            }

            var listing = golden.List().ToDictionary(p => p.Id, StringComparer.Ordinal);
            var truths = new Dictionary<string, TruthEntry>(StringComparer.Ordinal);
            var withoutTruth = new List<string>();
            foreach (var id in accepted.Select(r => r.PhotoId!).Distinct(StringComparer.Ordinal))
            {
                if (!listing.TryGetValue(id, out var photo))
                {
                    skipped.Add(new SkippedPhoto(id, "unknown_photo"));
                }
                else if (golden.ReadTruth(id) is not { } text)
                {
                    skipped.Add(new SkippedPhoto(id, "no_truth"));
                    withoutTruth.Add(id);
                }
                else if (TruthFiles.Parse(id, photo.Set, text) is { } truth)
                {
                    truths[id] = truth;
                }
                else
                {
                    skipped.Add(new SkippedPhoto(id, "invalid_truth"));
                }
            }

            // One memo per run, made here and passed down (not injected): what is grounded is known for this run only.
            var memo = new EvalGroundingMemo();
            var photos = new List<GroundedPhoto>();
            foreach (var result in accepted.Where(r => listing.ContainsKey(r.PhotoId!)))
            {
                var items = result.Outcome == EvalOutcome.Ok
                    ? await grounder.GroundAsync(result.Engine!.Value, result.Items ?? [], memo, cancellationToken)
                    : [];
                photos.Add(new GroundedPhoto(
                    result.PhotoId!,
                    EnumText.ToText(result.Engine!.Value),
                    result.FellBackFrom is { } reason ? EnumText.ToText(reason) : null,
                    EnumText.ToText(result.Outcome!.Value),
                    items,
                    result.UnreadableCount ?? 0,
                    result.LatencyMs!.Value,
                    result.FirstItemMs,
                    EnumText.ToText(result.Thermal!.Value),
                    result.CapturedCount ?? 0,
                    result.CapturedAt,
                    result.DuplicateRescan is { } evidence
                        ? new DuplicateRescan(evidence.OwnedHits, evidence.AnsweredOnCard, evidence.UnsolicitedAdds)
                        : null,
                    result.Engine == ScanEngine.VisionPipeline ? result.OcrLines?.Select(l => new GroundedOcrLine(l.Text!, l.X!.Value, l.Y!.Value, l.Width!.Value, l.Height!.Value)).ToList() : null,
                    result.Engine == ScanEngine.VisionPipeline ? result.OcrPieces?.Select(p => (IReadOnlyList<int>)[.. p]).ToList() : null,
                    EvalCoverCue.Clean(result.CoverCue)));
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Eval run grounded {Grounded} distinct reads and reused {Reused} repeats.", memo.Grounded, memo.Reused);
            }

            var run = new GroundedRun(
                ids.NewId().ToString("N"),
                clock.GetUtcNow(),
                new EvalDevice(input.Device!.Model!.Trim(), input.Device.OsVersion!.Trim(), input.Device.AppVersion!.Trim()),
                input.ConfigVersion!.Trim(),
                photos,
                input.Partial,
                string.IsNullOrWhiteSpace(input.BaselineNote) ? null : input.BaselineNote.Trim());
            var baseline = results.LoadBaselineDoc();
            var evaluated = EvalReport.Evaluate(
                run, truths, baseline?.Metrics, BuildSha.Resolve(options.Value), skipped, listing.Count,
                scoring.CurrentValue.Thresholds, baseline?.ThresholdHash);

            try
            {
                await results.SaveAsync(evaluated.Summary, run, evaluated.ReportMarkdown, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The exception's message names a path; only its type is logged.
                logger.LogWarning("The eval results could not be written ({Failure}).", exception.GetType().Name);
                return EvalErrors.ResultsNotWritable;
            }

            var drafts = WriteDrafts(withoutTruth, photos, listing);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Eval run {RunId}: scored {Scored} photos, skipped {Skipped}, wrote {Drafts} draft truth files; M3 gate {Verdict}.",
                    run.RunId,
                    evaluated.Summary.ScoredPhotos,
                    skipped.Count,
                    drafts,
                    evaluated.Summary.Gate.Verdict);
            }
            return new EvalRunResult(run.RunId, evaluated.Summary);
        }

        private int WriteDrafts(List<string> withoutTruth, List<GroundedPhoto> photos, Dictionary<string, GoldenPhotoDto> listing)
        {
            var written = 0;
            foreach (var id in withoutTruth)
            {
                if (golden.HasDraft(id))
                {
                    continue; // the owner may be correcting it
                }

                var best = photos.Where(p => p.PhotoId == id && p.Outcome == "ok" && p.Items.Count > 0)
                    .OrderByDescending(p => p.Items.Count(i => i.Verdict == "matched"))
                    .ThenByDescending(p => p.Items.Count)
                    .FirstOrDefault();
                if (best is null)
                {
                    continue;
                }

                var json = listing[id].Set == GoldenSets.Shelf ? ShelfDraft(best) : SingleDraft(best);
                if (golden.TryWriteDraft(id, json) || results.TryWriteDraft(id, json))
                {
                    written++;
                }
            }

            return written;
        }

        private static string ShelfDraft(GroundedPhoto best) => TruthFiles.WriteShelfDraft(
            best.Items.Select(i =>
            {
                var top = i.Best;
                return new ShelfTruthItem(top?.Title ?? i.ReadTitle ?? string.Empty, top?.Kind ?? "other", top?.Platform, top?.Year);
            }).Where(i => i.Title.Length > 0),
            best.UnreadableCount);

        private static string SingleDraft(GroundedPhoto best)
        {
            var item = best.Items[0];
            var top = item.Best;
            var format = Parse<ReleaseFormat>(top?.Format);
            var completeness = Parse<ItemCompleteness>(item.ReadCompleteness);
            return TruthFiles.WriteSingleDraft(new SingleTruth(
                top?.Title ?? item.ReadTitle ?? string.Empty,
                top?.Platform,
                top?.Format ?? "unknown",
                item.ReadCompleteness ?? "n_a",
                null,
                EvalKinds.Guess(format, completeness)));
        }

        private static T? Parse<T>(string? text)
            where T : struct, Enum
        {
            foreach (var value in Enum.GetValues<T>())
            {
                if (EnumText.ToText(value) == text)
                {
                    return value;
                }
            }

            return null;
        }

        private static DomainError? Validate(EvalRunInput input)
        {
            if (input.Device is not { } device || !Text(device.Model) || !Text(device.OsVersion) || !Text(device.AppVersion))
            {
                return EvalErrors.Invalid("evals.device_required", "Send the device: model, osVersion and appVersion (each up to 100 characters).", "device");
            }

            if (!Text(input.ConfigVersion))
            {
                return EvalErrors.Invalid("evals.config_version_required", "Send the configVersion the run used (up to 100 characters).", "configVersion");
            }

            if (input.Results is not { Count: > 0 })
            {
                return EvalErrors.Invalid("evals.results_required", "Send at least one photo result.", "results");
            }

            if (input.Results.Count > MaxPhotos)
            {
                return EvalErrors.Invalid("evals.too_many_results", $"Send at most {MaxPhotos} photo results in one run.", "results");
            }

            if (input.BaselineNote is not null && input.BaselineNote.Trim().Length > MaxNote)
            {
                return EvalErrors.Invalid("evals.note_too_long", $"A baseline note is at most {MaxNote} characters.", "baselineNote");
            }

            return null;
        }

        /// <summary>
        /// Checks each photo on its own. A photo that fails is skipped (listed with its problem code, logged at Warning) and the run
        /// goes on without it; one the checks had to change (items past the limit) is kept, with the change listed beside the skipped
        /// photos. A photo sent twice for one engine refuses the run. When no photo is left, the run is refused with the first
        /// photo's problem, so a 400 always names what was wrong.
        /// </summary>
        private (List<EvalPhotoInput> Photos, DomainError? Refusal) Screen(IReadOnlyList<EvalPhotoInput> results, List<SkippedPhoto> skipped)
        {
            var accepted = new List<EvalPhotoInput>(results.Count);
            var seen = new HashSet<(string, ScanEngine)>();
            DomainError? first = null;
            for (var i = 0; i < results.Count; i++)
            {
                var check = EvalPhotoChecks.Check(results[i], i);
                if (check.Problem is { } problem)
                {
                    first ??= problem;
                    skipped.Add(new SkippedPhoto(check.Label, problem.Code));
                    logger.LogWarning(
                        "Eval photo {PhotoId} ({Engine}) was skipped: {ProblemCode} at {Field}.",
                        check.Label,
                        results[i]?.Engine is { } engine ? EnumText.ToText(engine) : "(none)",
                        problem.Code,
                        problem.Field);
                    continue;
                }

                var photo = check.Photo!;
                if (!seen.Add((photo.PhotoId!, photo.Engine!.Value)))
                {
                    return ([], EvalErrors.Invalid("evals.duplicate_result", "Each photo can be sent once per engine.", $"results[{i}]"));
                }

                if (check.Note is { } note)
                {
                    skipped.Add(new SkippedPhoto(check.Label, note));
                    logger.LogWarning("Eval photo {PhotoId} ({Engine}) was scored with a change: {Note}.", check.Label, EnumText.ToText(photo.Engine.Value), note);
                }

                accepted.Add(photo);
            }

            return accepted.Count == 0 ? ([], first) : (accepted, null);
        }

        private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxText;
    }
}

/// <summary>
/// What one photo's own checks found. <see cref="Photo"/> is the photo to score (cleaned), or null when it failed
/// (<see cref="Problem"/>). <see cref="Note"/> says what the checks cut from a photo that is still scored. <see cref="Label"/> names the
/// photo in the skipped list and the log: its id when that is a valid id, else its place in the run (<c>results[3]</c>), so a bad id never
/// reaches a file or a log line.
/// </summary>
public sealed record EvalPhotoCheck(EvalPhotoInput? Photo, DomainError? Problem, string? Note, string Label);

/// <summary>
/// The checks on one photo of an eval run. They keep the run safe to ground and write, and no more: a photo is the phone's one chance to
/// have read a shelf, so what can be cleaned is cleaned and only what cannot be is a problem (and then only that photo is skipped).
/// <list type="bullet">
/// <item>An OCR box value within <see cref="BoxSlack"/> of 0 to 1 is clamped into range (the 180 degree pass maps a box as 1 - (x + width), which rounds just outside).</item>
/// <item>A product code the server's shape check refuses is dropped; the item stays.</item>
/// <item>Items past <see cref="IdentifyCleaning.MaxItems"/> are cut (the first ones, in reading order, are kept) and the cut is the note.</item>
/// </list>
/// </summary>
public static class EvalPhotoChecks
{
    /// <summary>How far outside 0 to 1 an OCR box value may be and still be clamped, not refused.</summary>
    public const double BoxSlack = 0.01;

    private const int MaxCounted = 1000;
    private const int MaxOcrLines = 1000;
    private const int MaxOcrText = 200;
    private const long MaxMillis = 3_600_000;
    private const double Epsilon = 1e-9; // 1.01 - 1 is 0.010000000000000009 in doubles

    public static int MaxItems => IdentifyCleaning.MaxItems;

    public static EvalPhotoCheck Check(EvalPhotoInput? photo, int index)
    {
        var at = $"results[{index}]";
        var label = EvalIds.IsValid(photo?.PhotoId) ? photo!.PhotoId! : at;
        if (Problem(photo, at) is { } problem)
        {
            return new EvalPhotoCheck(null, problem, null, label);
        }

        var cleaned = photo!;
        if (cleaned.Engine == ScanEngine.VisionPipeline)
        {
            var ocr = CleanOcr(cleaned, at, out var ocrProblem);
            if (ocrProblem is not null)
            {
                return new EvalPhotoCheck(null, ocrProblem, null, label);
            }

            cleaned = ocr!;
        }

        if (cleaned.Items is not { Count: > 0 })
        {
            return new EvalPhotoCheck(cleaned, null, null, label); // a refusal, a failure or an empty reading carries no items
        }

        if (cleaned.Items.Any(item => item?.Read is null))
        {
            return Failed(EvalErrors.Invalid("evals.item_invalid", "An item was empty.", $"{at}.items"), label);
        }

        for (var j = 0; j < cleaned.Items.Count; j++)
        {
            if (cleaned.Items[j].Tier is { } tier && !Enum.IsDefined(tier))
            {
                return Failed(EvalErrors.Invalid("evals.tier_invalid", "A tier is T0, T1 or T2.", $"{at}.items[{j}].tier"), label);
            }
        }

        string? note = null;
        var items = cleaned.Items;
        if (items.Count > MaxItems)
        {
            note = $"trimmed_items_{MaxItems}_of_{items.Count}";
            items = [.. items.Take(MaxItems)];
        }

        items = [.. items.Select(WithoutBadProductCode)];

        // The size limit of /identify (16 KB of text an item) is the one check left to it: items and codes are handled above.
        var check = new IdentifyInput(Guid.NewGuid(), SnapTrigger.Import, cleaned.Engine, null, [.. items.Select(i => i.Read)]);
        return IdentifyCleaning.Validate(check) is { } tooBig
            ? Failed(EvalErrors.Invalid(tooBig.Code, tooBig.Message, $"{at}.items"), label)
            : new EvalPhotoCheck(cleaned with { Items = items }, null, note, label);
    }

    private static EvalPhotoCheck Failed(DomainError problem, string label) => new(null, problem, null, label);

    private static EvalReadItemInput WithoutBadProductCode(EvalReadItemInput item) =>
        !string.IsNullOrWhiteSpace(item.Read.ProductCode) && !ProductCode.IsAcceptable(item.Read.ProductCode)
            ? item with { Read = item.Read with { ProductCode = null } }
            : item;

    private static DomainError? Problem(EvalPhotoInput? photo, string at)
    {
        if (!EvalIds.IsValid(photo?.PhotoId))
        {
            return EvalErrors.Invalid("evals.photo_id_invalid", "A photoId is letters, digits, _ and -, up to 64 characters.", $"{at}.photoId");
        }

        if (photo!.Engine is null)
        {
            return EvalErrors.Invalid("evals.engine_required", "Send the engine: apple_model, fm_text or vision_pipeline.", $"{at}.engine");
        }

        if (photo.Outcome is null)
        {
            return EvalErrors.Invalid("evals.outcome_required", "Send the outcome: ok, refused, timeout or error.", $"{at}.outcome");
        }

        if (photo.Thermal is null)
        {
            return EvalErrors.Invalid("evals.thermal_required", "Send the thermalState: nominal, fair, serious or critical.", $"{at}.thermalState");
        }

        if (photo.LatencyMs is not { } latency || latency is < 0 or > MaxMillis || photo.FirstItemMs is < 0 or > MaxMillis)
        {
            return EvalErrors.Invalid("evals.latency_invalid", "latencyMs and firstItemMs are milliseconds, 0 or more.", $"{at}.latencyMs");
        }

        if (photo.UnreadableCount is < 0 or > MaxCounted)
        {
            return EvalErrors.Invalid("evals.unreadable_invalid", "unreadableCount is 0 or more.", $"{at}.unreadableCount");
        }

        if (photo.CapturedCount is < 0 or > MaxCounted)
        {
            return EvalErrors.Invalid("evals.captured_invalid", "capturedCount is 0 or more.", $"{at}.capturedCount");
        }

        if (photo.DuplicateRescan is { } evidence
            && (evidence.OwnedHits is < 0 or > MaxCounted
                || evidence.AnsweredOnCard is < 0
                || evidence.AnsweredOnCard > evidence.OwnedHits
                || evidence.UnsolicitedAdds is < 0 or > MaxCounted))
        {
            return EvalErrors.Invalid(
                "evals.duplicate_invalid",
                "A duplicate re-scan reports owned hits, how many were answered on-card (at most the hits), and unsolicited adds.",
                $"{at}.duplicateRescan");
        }

        return null;
    }

    /// <summary>
    /// The Vision photo with its OCR lines cleaned: at most 1000, text up to 200 characters, box values 0 to 1 (a value within
    /// <see cref="BoxSlack"/> outside is clamped in); each piece points at lines that exist. Anything else is the problem.
    /// </summary>
    private static EvalPhotoInput? CleanOcr(EvalPhotoInput photo, string at, out DomainError? problem)
    {
        problem = null;
        var lines = photo.OcrLines;
        if (lines is { Count: > MaxOcrLines })
        {
            problem = EvalErrors.Invalid("evals.ocr_too_many_lines", $"Send at most {MaxOcrLines} OCR lines per photo.", $"{at}.ocrLines");
            return null;
        }

        var clamped = new List<EvalOcrLineInput>(lines?.Count ?? 0);
        for (var i = 0; i < (lines?.Count ?? 0); i++)
        {
            if (Clamp(lines![i]) is not { } line)
            {
                problem = EvalErrors.Invalid(
                    "evals.ocr_line_invalid",
                    $"An OCR line is its text (up to {MaxOcrText} characters) and a box with x, y, width and height, each from 0 to 1.",
                    $"{at}.ocrLines[{i}]");
                return null;
            }

            clamped.Add(line);
        }

        var available = clamped.Count;
        if (photo.OcrPieces is { } pieces
            && (pieces.Count > MaxOcrLines || pieces.Any(piece => piece is null || piece.Count > MaxOcrLines || piece.Any(index => index < 0 || index >= available))))
        {
            problem = EvalErrors.Invalid(
                "evals.ocr_piece_invalid", "A piece is the indexes of OCR lines that were sent with it.", $"{at}.ocrPieces");
            return null;
        }

        return lines is null ? photo : photo with { OcrLines = clamped };
    }

    /// <summary>The line with its box values clamped into 0 to 1, or null when it has no text, too much, or a value too far outside.</summary>
    private static EvalOcrLineInput? Clamp(EvalOcrLineInput? line)
    {
        if (line is not { Text.Length: <= MaxOcrText } || Clamp(line.X) is not { } x || Clamp(line.Y) is not { } y
            || Clamp(line.Width) is not { } width || Clamp(line.Height) is not { } height)
        {
            return null;
        }

        return line with { X = x, Y = y, Width = width, Height = height };
    }

    private static double? Clamp(double? value) =>
        value is { } v && v >= -BoxSlack - Epsilon && v <= 1 + BoxSlack + Epsilon ? Math.Clamp(v, 0, 1) : null;
}

/// <summary>The git sha that names a result file: configured, else the deployed image's tag, else the build's own version.</summary>
public static class BuildSha
{
    public static string Resolve(EvalsOptions options)
    {
        foreach (var candidate in new[]
        {
            options.BuildSha,
            Environment.GetEnvironmentVariable("CM_TAG"),
            typeof(BuildSha).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+').ElementAtOrDefault(1),
        })
        {
            if (Clean(candidate) is { } sha)
            {
                return sha;
            }
        }

        return "dev";
    }

    /// <summary>Letters and digits only (it ends up in a file name), at most 12; null when nothing is left.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string([.. value.Trim().Where(char.IsAsciiLetterOrDigit).Take(12)]);
        return cleaned.Length == 0 ? null : cleaned.ToLowerInvariant();
    }
}
