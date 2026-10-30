using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// Grounds what the phone read in a golden photo with the very code <c>/identify</c> uses (<see cref="IdentifyGrounding"/> and the
/// same cleaning and profile choice), but as a dry run: there is no scan session, and no snap, candidate or release row is
/// written, no artwork re-score is asked for and no background job is queued. The providers are asked through their normal cached
/// path, so a repeat costs nothing. It reads the catalogue as the caller can: a signed-in administrator sees the global releases.
/// </summary>
public sealed class EvalGrounder(IdentifyGrounding grounding, IAppDbContext db, ScanningOptions options, IGameSerialLookup serials)
{
    /// <summary>The top releases kept per item (the same three <c>/identify</c> offers).</summary>
    public const int Alternatives = 3;

    /// <summary>
    /// Grounds the items one photo and engine read. A read the run's <paramref name="memo"/> has already grounded is answered from
    /// it, so the catalogue and the providers are asked once per distinct read in a run, not once per engine and photo.
    /// </summary>
    public async Task<IReadOnlyList<GroundedItem>> GroundAsync(
        ScanEngine engine, IReadOnlyList<EvalReadItemInput> items, EvalGroundingMemo memo, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var cleaned = IdentifyCleaning.Clean([.. items.Select(i => i.Read)], serials: serials);
        var answers = new List<BarcodeResolution>(items.Count);
        foreach (var item in cleaned)
        {
            // No one is waiting on an eval, so each read gets the patient budget the background job gets, not the live request's.
            answers.Add(await memo.GroundAsync(
                item.Facts, item.Profile, () => grounding.GroundAsync(item.Facts, item.Profile, options.JobResolveBudget, cancellationToken)));
        }

        // One book, one item per photo and engine: a read that repeats a stronger read of the same release is not scored, the same
        // rule /identify applies, so the scorer sees what a person scanning this photo would have been shown.
        var repeats = RepeatReads.Of(answers).ToHashSet();
        var local = await LoadLocalAsync(answers, cancellationToken);
        var grounded = new List<GroundedItem>(items.Count - repeats.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (repeats.Contains(i))
            {
                continue;
            }

            var answer = answers[i];
            var candidates = answer.Options
                .Take(Alternatives)
                .Select(o => Candidate(o, local))
                .OfType<GroundedCandidate>()
                .ToList();
            grounded.Add(new GroundedItem(
                cleaned[i].Facts.SearchText,
                items[i].Read.Completeness is { } completeness ? EnumText.ToText(completeness) : null,
                ResolvedByText.For(engine, items[i].ResolvedBy),
                EnumText.ToText(answer.Verdict),
                candidates,
                items[i].Tier is { } tier ? EnumText.ToText(tier) : null,
                items[i].Provisional,
                items[i].IsGuess,
                cleaned[i].SegmentId,
                ReadOf(cleaned[i].Facts, cleaned[i].Profile, items[i].Read.Creator)));
        }

        return grounded;
    }

    /// <summary>What the read claimed besides its title, as it was scored, the profile it was scored with, and the author as read.</summary>
    private static GroundedRead ReadOf(ReadFacts facts, ScoringProfileKind profile, string? creatorAsRead) => new(
        facts.Creator,
        facts.Kind is { } kind ? EnumText.ToText(kind) : null,
        facts.Format is { } format ? EnumText.ToText(format) : null,
        facts.Year,
        facts.Platform,
        facts.Edition,
        EnumText.ToText(profile),
        creatorAsRead);

    private static GroundedCandidate? Candidate(ResolutionOption option, Dictionary<Guid, GroundedCandidate> local)
    {
        if (option.LocalReleaseId is { } id)
        {
            return local.TryGetValue(id, out var found) ? found with { Score = option.Score, Terms = option.Terms } : null;
        }

        var provider = option.Provider;
        return provider is null
            ? null
            : new GroundedCandidate(
                provider.Title,
                EnumText.ToText(provider.Kind),
                provider.Format is { } format ? EnumText.ToText(format) : null,
                provider.Platform,
                provider.Year,
                option.Score,
                provider.Creators,
                option.Terms);
    }

    private async Task<Dictionary<Guid, GroundedCandidate>> LoadLocalAsync(List<BarcodeResolution> answers, CancellationToken cancellationToken)
    {
        var ids = answers.SelectMany(a => a.Options).Select(o => o.LocalReleaseId).OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var releases = await db.Releases.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        var workIds = releases.Select(r => r.WorkId).Distinct().ToList();
        var works = await db.Works.AsNoTracking().Where(w => workIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        var platformIds = releases.Select(r => r.PlatformId).OfType<Guid>().Distinct().ToList();
        var platforms = platformIds.Count == 0
            ? []
            : await db.Platforms.AsNoTracking().Where(p => platformIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        return releases.ToDictionary(
            r => r.Id,
            r => new GroundedCandidate(
                works[r.WorkId].Title,
                EnumText.ToText(works[r.WorkId].Kind),
                EnumText.ToText(r.Format),
                r.PlatformId is { } platformId && platforms.TryGetValue(platformId, out var platform) ? platform.Name : null,
                r.ReleaseDate?.Year ?? works[r.WorkId].OriginalYear,
                0,
                ItemProjection.Creators(works[r.WorkId].CreatorsJson)));
    }
}
