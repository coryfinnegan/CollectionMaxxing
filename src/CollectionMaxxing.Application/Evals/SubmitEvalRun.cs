using System.Reflection;
using CollectionMaxxing.Application.Common;
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
/// </summary>
public static class SubmitEvalRun
{
    public const int MaxPhotos = 400;
    private const int MaxText = 100;
    private const int MaxNote = 500;
    private const int MaxCounted = 1000;
    private const long MaxMillis = 3_600_000;

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

            var listing = golden.List().ToDictionary(p => p.Id, StringComparer.Ordinal);
            var truths = new Dictionary<string, TruthEntry>(StringComparer.Ordinal);
            var skipped = new List<SkippedPhoto>();
            var withoutTruth = new List<string>();
            foreach (var id in input.Results!.Select(r => r.PhotoId!).Distinct(StringComparer.Ordinal))
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

            var photos = new List<GroundedPhoto>();
            foreach (var result in input.Results!.Where(r => listing.ContainsKey(r.PhotoId!)))
            {
                var items = result.Outcome == EvalOutcome.Ok
                    ? await grounder.GroundAsync(result.Engine!.Value, result.Items ?? [], cancellationToken)
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
                        : null));
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

            var seen = new HashSet<(string, ScanEngine)>();
            for (var i = 0; i < input.Results.Count; i++)
            {
                if (ValidatePhoto(input.Results[i], i) is { } problem)
                {
                    return problem;
                }

                if (!seen.Add((input.Results[i].PhotoId!, input.Results[i].Engine!.Value)))
                {
                    return EvalErrors.Invalid("evals.duplicate_result", "Each photo can be sent once per engine.", $"results[{i}]");
                }
            }

            return null;
        }

        private static DomainError? ValidatePhoto(EvalPhotoInput photo, int index)
        {
            var at = $"results[{index}]";
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

            if (photo.Items is not { Count: > 0 })
            {
                return null; // a refusal, a failure or an empty reading carries no items
            }

            if (photo.Items.Any(item => item?.Read is null))
            {
                return EvalErrors.Invalid("evals.item_invalid", "An item was empty.", $"{at}.items");
            }

            for (var j = 0; j < photo.Items.Count; j++)
            {
                if (photo.Items[j]!.Tier is { } tier && !Enum.IsDefined(tier))
                {
                    return EvalErrors.Invalid("evals.tier_invalid", "A tier is T0, T1 or T2.", $"{at}.items[{j}].tier");
                }
            }

            // The same limits as /identify: at most 40 items, and 16 KB of text each.
            var check = new IdentifyInput(Guid.NewGuid(), SnapTrigger.Import, photo.Engine, null, [.. photo.Items.Select(i => i.Read)]);
            return IdentifyCleaning.Validate(check) is { } problem
                ? EvalErrors.Invalid(problem.Code, problem.Message, $"{at}.items")
                : null;
        }

        private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxText;
    }
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
