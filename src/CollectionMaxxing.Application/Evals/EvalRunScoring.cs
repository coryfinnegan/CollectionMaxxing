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

/// <summary>The golden photos a run covers, the truth of those that have a valid truth file, and the ones that have none (a draft is written for them).</summary>
public sealed record EvalTruthSet(
    IReadOnlyDictionary<string, GoldenPhotoDto> Listing,
    IReadOnlyDictionary<string, TruthEntry> Truths,
    IReadOnlyList<string> WithoutTruth);

/// <summary>
/// A run ready to be scored: who ran it, and every engine result of every photo, grounded. <c>RegroundedFrom</c> is set when the PC made the run by
/// itself from an earlier run's kept phone readings: the summary carries it, the report says so, and no draft truth files are written for it.
/// </summary>
public sealed record EvalRunToScore(
    string RunId,
    DateTimeOffset At,
    EvalDevice Device,
    string ConfigVersion,
    bool Partial,
    string? BaselineNote,
    IReadOnlyList<GroundedPhoto> Photos,
    EvalRegroundedFrom? RegroundedFrom = null);

/// <summary>
/// The two halves of a run that every way of making one shares: grounding what the phone read in one photo through the code <c>/identify</c> uses,
/// and scoring grounded photos against the truth files with the live scoring options, writing the result files, the report and the draft truth files.
/// The one-shot route (<see cref="SubmitEvalRun"/>) does both inside its request; the resumable run grounds each photo in the background as it arrives and scores
/// once when the phone finishes. Both write the same files.
/// </summary>
public sealed class EvalRunScoring(
    IGoldenSet golden,
    IEvalResults results,
    EvalGrounder grounder,
    IOptions<EvalsOptions> options,
    IOptionsMonitor<ScoringOptions> scoring,
    ILogger<EvalRunScoring> logger)
{
    /// <summary>Grounds the items one engine read in one photo, as a dry run (nothing is persisted), and keeps what the PC knows of the reading.</summary>
    public async Task<GroundedPhoto> GroundAsync(EvalPhotoInput result, EvalGroundingMemo memo, CancellationToken cancellationToken)
    {
        var items = result.Outcome == EvalOutcome.Ok
            ? await grounder.GroundAsync(result.Engine!.Value, result.Items ?? [], memo, cancellationToken)
            : [];
        return new GroundedPhoto(
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
            EvalCoverCue.Clean(result.CoverCue));
    }

    /// <summary>
    /// Looks the photos up in the golden folder: the ones it does not list, and the ones with no truth file or an invalid one, are added to
    /// <paramref name="skipped"/> (in the order of <paramref name="photoIds"/>), and the rest get their truth.
    /// </summary>
    public EvalTruthSet Prepare(IEnumerable<string> photoIds, List<SkippedPhoto> skipped)
    {
        var listing = golden.List().ToDictionary(p => p.Id, StringComparer.Ordinal);
        var truths = new Dictionary<string, TruthEntry>(StringComparer.Ordinal);
        var withoutTruth = new List<string>();
        foreach (var id in photoIds)
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

        return new EvalTruthSet(listing, truths, withoutTruth);
    }

    /// <summary>
    /// Scores the run, writes its result files and the draft truth files (none for a run that is <see cref="EvalRunToScore.RegroundedFrom"/> an earlier
    /// one), and returns the summary. Fails only when the results folder cannot be written, and then nothing is half written.
    /// </summary>
    public async Task<Result<EvalRunResult>> ScoreAsync(
        EvalRunToScore run, EvalTruthSet truthSet, IReadOnlyList<SkippedPhoto> skipped, CancellationToken cancellationToken)
    {
        var grounded = new GroundedRun(
            run.RunId,
            run.At,
            run.Device,
            run.ConfigVersion,
            run.Photos,
            run.Partial,
            string.IsNullOrWhiteSpace(run.BaselineNote) ? null : run.BaselineNote.Trim());
        cancellationToken.ThrowIfCancellationRequested(); // stopping is fine before the files start to be written, not in the middle of them
        var baseline = results.LoadBaselineDoc();
        var evaluated = EvalReport.Evaluate(
            grounded, truthSet.Truths, baseline?.Metrics, BuildSha.Resolve(options.Value), skipped, truthSet.Listing.Count,
            scoring.CurrentValue.Thresholds, baseline?.ThresholdHash, run.RegroundedFrom);

        try
        {
            // Once writing has started it finishes: a stop half way would leave some of the four files, and the next try would write the run again.
            await results.SaveAsync(evaluated.Summary, grounded, evaluated.ReportMarkdown, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The exception's message names a path; only its type is logged.
            logger.LogWarning("The eval results could not be written ({Failure}).", exception.GetType().Name);
            return EvalErrors.ResultsNotWritable;
        }

        // A re-score is the PC's own work on readings it already drafted truth for: it leaves the golden folder as the owner has it.
        var drafts = run.RegroundedFrom is null ? WriteDrafts(truthSet.WithoutTruth, run.Photos, truthSet.Listing) : 0;
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

    private int WriteDrafts(IReadOnlyList<string> withoutTruth, IReadOnlyList<GroundedPhoto> photos, IReadOnlyDictionary<string, GoldenPhotoDto> listing)
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
        var format = EnumText.ParseOrNull<ReleaseFormat>(top?.Format);
        var completeness = EnumText.ParseOrNull<ItemCompleteness>(item.ReadCompleteness);
        return TruthFiles.WriteSingleDraft(new SingleTruth(
            top?.Title ?? item.ReadTitle ?? string.Empty,
            top?.Platform,
            top?.Format ?? "unknown",
            item.ReadCompleteness ?? "n_a",
            null,
            EvalKinds.Guess(format, completeness)));
    }
}
