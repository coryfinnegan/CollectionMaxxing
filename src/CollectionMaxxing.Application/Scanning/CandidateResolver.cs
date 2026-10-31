using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Ownership;
using CollectionMaxxing.Application.Scanning.Artwork;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Application.Scanning.Quota;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// The database half of resolving a candidate: the catalogue's own answer to a barcode, and writing any answer onto the
/// candidate (the matched release, the top three, who already owns it, and what to do by default). Not thread-safe: use it
/// from one request at a time, and run the providers (<see cref="BarcodeResolver"/>) beside it.
/// </summary>
public sealed class CandidateResolver(
    IAppDbContext db, ReleaseStore releases, OwnershipQuery ownership, TimeProvider clock, IOptionsMonitor<ScoringOptions> scoring)
{
    private const int MaxAlternatives = 3;

    private static readonly TraceStep LocalHit = new("catalogue", "hit", 0);

    /// <summary>The releases the catalogue already has for each barcode (docs/02 section 3, step 1), by the barcode's digits.</summary>
    public async Task<Dictionary<string, List<Guid>>> FindLocalAsync(IReadOnlyCollection<Identifier> identifiers, CancellationToken cancellationToken)
    {
        if (identifiers.Count == 0)
        {
            return [];
        }

        var values = identifiers.Select(i => i.Value).Distinct().ToList();

        // A 13-digit code may be stored as an ISBN or as a GTIN; the digits identify the product. The release filter hides other libraries' releases.
        var rows = await (from i in db.ReleaseIdentifiers.AsNoTracking()
                          join r in db.Releases.AsNoTracking() on i.ReleaseId equals r.Id
                          where values.Contains(i.Value)
                          select new { i.Value, r.Id }).Distinct().ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.Value).ToDictionary(g => g.Key, g => g.Select(r => r.Id).Order().ToList());
    }

    /// <summary>An answer from the catalogue itself: one release is a sure match; several (a reused barcode) make the person pick.</summary>
    public static BarcodeResolution LocalResolution(IReadOnlyList<Guid> releaseIds) => new(
        releaseIds.Count == 1 ? ResolutionVerdict.Matched : ResolutionVerdict.Ambiguous,
        releaseIds.Take(MaxAlternatives).Select(id => new ResolutionOption(id, null, 1.0)).ToList(),
        null,
        [LocalHit]);

    /// <summary>An answer from the catalogue itself, after a cover re-ask whose steps join the trace (#506).</summary>
    public static BarcodeResolution LocalResolution(IReadOnlyList<Guid> releaseIds, IReadOnlyList<BookLookupStep> reasked) =>
        reasked.Count == 0
            ? LocalResolution(releaseIds)
            : new(
                releaseIds.Count == 1 ? ResolutionVerdict.Matched : ResolutionVerdict.Ambiguous,
                releaseIds.Take(MaxAlternatives).Select(id => new ResolutionOption(id, null, 1.0)).ToList(),
                null,
                [LocalHit, .. reasked.Select(s => new TraceStep(s.Source, s.Outcome, s.Ms))]);

    /// <summary>Which of the releases have no cover and name no cover address: the scan re-asks the providers for those (#506).</summary>
    public async Task<HashSet<Guid>> FindCoverlessAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        if (releaseIds.Count == 0)
        {
            return [];
        }

        var rows = await db.Releases.AsNoTracking()
            .Where(r => releaseIds.Contains(r.Id))
            .Select(r => new { r.Id, r.CoverAssetId, r.ProviderPayloadsJson })
            .ToListAsync(cancellationToken);
        return rows
            .Where(r => r.CoverAssetId is null && CoverOutcome.CoverUrlOf(r.ProviderPayloadsJson) is null)
            .Select(r => r.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Writes an answer onto the candidate: the resolution, the commit tier (docs/16 §6), the matched release, the top
    /// three, who already owns it, and what to do by default. Saves nothing; the caller saves.
    /// <paramref name="fromIdentifier"/> is true when the answer comes from a barcode (a normalized identifier), the only
    /// path that can reach T0. <paramref name="copiesMode"/> is true when the session opted into copies: an exact-owned
    /// barcode hit is accepted (and T0) instead of skipped; a T1 never is (docs/16 §15).
    /// </summary>
    public async Task ApplyAsync(
        Guid libraryId, ScanCandidate candidate, BarcodeResolution resolution, CancellationToken cancellationToken,
        bool fromIdentifier = false, bool copiesMode = false)
    {
        // The options' releases are ensured before the verdict is final: the veto and the tier read what the options
        // point at (a local option carries no title of its own), and a vetoed match is Ambiguous however high it scored.
        var ensured = new List<EnsuredRelease>();
        if (resolution.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous)
        {
            var (reunited, donorCovers) = await CollapseSameNovelAsync(resolution, cancellationToken);
            resolution = reunited;
            foreach (var option in resolution.Options.Take(MaxAlternatives))
            {
                ensured.Add(option.LocalReleaseId is { } local
                    ? new EnsuredRelease(local, false)
                    : await releases.EnsureAsync(option.Provider!, cancellationToken));
            }

            await ShareSameNovelCoversAsync(ensured, donorCovers, cancellationToken);
        }

        var taken = resolution.Options.Take(MaxAlternatives).Zip(ensured, (option, release) => (option, release.Id)).ToList();
        var facts = await ReleaseFactsAsync(ensured, cancellationToken);
        var verdict = VetoApplies(resolution, taken, facts) ? ResolutionVerdict.Ambiguous : resolution.Verdict;
        candidate.Resolution = verdict switch
        {
            ResolutionVerdict.Matched => CandidateResolution.Matched,
            ResolutionVerdict.Ambiguous => CandidateResolution.Ambiguous,
            ResolutionVerdict.NotFound => CandidateResolution.NotFound,
            _ => CandidateResolution.Pending,
        };
        candidate.MatchedReleaseId = null;
        candidate.ArtDistance = null; // an answer is about the releases it names; the artwork handler sets this after it
        candidate.Score = null;
        candidate.AlternativesJson = null;
        candidate.AlreadyOwnedCount = 0;
        candidate.GuidanceCode = Reguide(candidate, resolution);
        var undecided = candidate.Decision == CandidateDecision.Undecided; // a decision the person already made is theirs; the job never overwrites it
        var ownedAt = new List<string>();
        CandidateDecision? autoDecision = null;
        var firstOwned = 0;

        var ownershipBiased = false;
        var coverBiased = false;
        if (verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous)
        {
            var alternatives = new List<StoredAlternative>();
            var known = await CandidateProjection.LoadReleasesAsync(
                db, ensured.Where(e => !e.IsNew).Select(e => e.Id).ToList(), cancellationToken);
            var ownership = new List<OwnershipDto>(ensured.Count);
            foreach (var release in ensured)
            {
                ownership.Add(await OwnedAsync(libraryId, release, cancellationToken));
            }

            // #516: a close call lists what the library already owns first, so a
            // same-title book the owner holds is never buried under an unowned one
            // by scoring noise. Only the order changes; the verdict and every
            // score stay exactly as the scorer computed them.
            var order = BiasTowardOwned(resolution.Options, ownership, verdict);
            ownershipBiased = order is not null;
            order ??= Enumerable.Range(0, ensured.Count).ToList();

            // #519: an exact-score tie lists what has a stored cover first, so
            // the top pick is one the person can verify against its picture.
            // Only ties move; a real lead (text or artwork) always wins.
            var covered = new List<bool>(ensured.Count);
            for (var i = 0; i < ensured.Count; i++)
            {
                covered.Add(CoveredNow(resolution.Options, ensured, known, i));
            }

            var coverOrder = BreakTiesTowardCovered(resolution.Options, ownership, covered, order, verdict);
            coverBiased = coverOrder is not null;
            if (coverOrder is not null)
            {
                order = coverOrder;
            }

            for (var rank = 0; rank < order.Count; rank++)
            {
                var i = order[rank];
                var option = resolution.Options[i];
                var owned = ownership[i];
                if (rank == 0 && verdict == ResolutionVerdict.Matched)
                {
                    candidate.MatchedReleaseId = ensured[i].Id;
                    candidate.Score = option.Score;
                    candidate.AlreadyOwnedCount = owned.Count;
                    ownedAt = OwnedAt(owned);
                    firstOwned = owned.Count;

                    // An exactly owned release is skipped unless the session opted into copies; anything else is added.
                    if (undecided)
                    {
                        autoDecision = owned.Count > 0 && !copiesMode ? CandidateDecision.Rejected : CandidateDecision.Accepted;
                        candidate.Decision = autoDecision.Value;
                    }
                }

                if (rank == 0 && verdict == ResolutionVerdict.Ambiguous)
                {
                    candidate.Score = option.Score;
                }

                alternatives.Add(AlternativeOf(option, ensured[i].Id, known, owned.Count));
            }

            candidate.AlternativesJson = CandidateJson.Serialize(alternatives);
        }

        candidate.Tier = EvaluateTier(
            verdict, resolution, taken, facts, firstOwned, fromIdentifier, copiesMode, candidate.ShotKind,
            StoredRead.IsUnconfirmed(candidate.InferredJson));

        if (resolution.ProductTitle is { } productTitle && candidate.VisibleText is null)
        {
            SetVisibleText(candidate, productTitle);
        }
        else if (UpcQuotaWindow.IsSpent(resolution) && candidate.VisibleText is null
            && Identifier.TryParse(candidate.RawCode, out var quotaCode))
        {
            // A barcode-only provisional (design §12): the code is kept, counted and duplicate-checked by its
            // digits, and the drain looks it up when the quota resets. The text is write-once, like any other.
            SetVisibleText(candidate, QuotaProvisionalText(quotaCode.Value));
        }

        // A code that found the UPC allowance spent waits for the nightly drain: say when it will next be tried.
        DateTimeOffset? retryAfter = UpcQuotaWindow.IsSpent(resolution) ? UpcQuotaWindow.NextResetAfter(clock.GetUtcNow()) : null;
        var steps = resolution.Steps;
        if (ownershipBiased)
        {
            steps = [.. steps, new TraceStep("ownership", "biased", 0)];
        }

        if (coverBiased)
        {
            steps = [.. steps, new TraceStep("cover", "biased", 0)];
        }
        candidate.TraceJson = CandidateJson.Serialize(new StoredTrace(
            steps, ownedAt, NeedsArt(candidate, resolution with { Verdict = verdict }), autoDecision, retryAfter));
    }

    /// <summary>
    /// How much higher an exactly-owned release sorts in a close call, in score units. Small on purpose: it settles
    /// close calls, never landslides — and it can never promote past a Matched lead (0.08 by default), so a sure
    /// thing stays sure. Like the context bias, the verdict and every score stay exactly as the scorer computed
    /// them; only the order changes.
    /// </summary>
    public const double OwnedBias = 0.05;

    /// <summary>
    /// The display order of a close call's options: exactly-owned releases sort higher by <see cref="OwnedBias"/>,
    /// stable (a gap the bonus exactly closes keeps scorer order). Null when the verdict is not Ambiguous, nothing
    /// is owned, or the bias changes nothing, so the caller keeps scorer order. Pure.
    /// </summary>
    internal static List<int>? BiasTowardOwned(
        IReadOnlyList<ResolutionOption> options, IReadOnlyList<OwnershipDto> ownership, ResolutionVerdict verdict)
    {
        if (verdict != ResolutionVerdict.Ambiguous || ownership.All(o => o.Count == 0))
        {
            return null;
        }

        var order = Enumerable.Range(0, ownership.Count)
            .OrderByDescending(i => options[i].Score + (ownership[i].Count > 0 ? OwnedBias : 0))
            .ToList();
        return order.SequenceEqual(Enumerable.Range(0, ownership.Count)) ? null : order;
    }

    /// <summary>
    /// Whether option <paramref name="index"/> shows a stored cover right now: its release (or work) already has a cover
    /// asset. A provider option never does: its cover address is a promise the commit or backfill has not kept yet, and
    /// counting it would let a provider shadow jump the learned row it reunites with (#514). A same-novel share this same
    /// answer just made counts only once saved, like #515's one-scan-late display. Pure.
    /// </summary>
    private static bool CoveredNow(
        IReadOnlyList<ResolutionOption> options, List<EnsuredRelease> ensured,
        Dictionary<Guid, Catalog.ReleaseDto> known, int index) =>
        options[index].Provider is null
        && known.TryGetValue(ensured[index].Id, out var release)
        && release.CoverUrl is not null;

    /// <summary>
    /// The display order of a close call's exact-score ties: options with a stored cover sort first, so the top pick is
    /// one the person can verify against its picture (#519). Only ties on the owned-biased score move — a real lead
    /// (text or artwork) always wins, and ties otherwise keep their order. Null when the verdict is not Ambiguous or no
    /// tie moves, so the caller keeps the owned-biased order. Pure.
    /// </summary>
    internal static List<int>? BreakTiesTowardCovered(
        IReadOnlyList<ResolutionOption> options, IReadOnlyList<OwnershipDto> ownership, IReadOnlyList<bool> covered,
        IReadOnlyList<int> order, ResolutionVerdict verdict)
    {
        if (verdict != ResolutionVerdict.Ambiguous)
        {
            return null;
        }

        var reordered = order
            .OrderByDescending(i => options[i].Score + (ownership[i].Count > 0 ? OwnedBias : 0))
            .ThenByDescending(i => covered[i])
            .ToList();
        return reordered.SequenceEqual(order) ? null : reordered;
    }

    /// <summary>
    /// Reunites a provider option with the catalogue's own same-novel release (#514): one provider keeps a novel
    /// under several work keys, and a rescan that matched the confirmed row must not mint a parallel row for the
    /// same novel beside it. A reunited option points at the rank-first local same-novel release (its score is
    /// kept) and drops the provider payload; the payload's cover address is kept as a donor for the cover pass,
    /// by release id in option order. A provider option with no same-novel local release (a first scan, or
    /// another author's book) is untouched and ensures exactly as before. Saves nothing; the caller saves.
    /// </summary>
    private async Task<(BarcodeResolution Resolution, Dictionary<Guid, List<string>> DonorCovers)> CollapseSameNovelAsync(
        BarcodeResolution resolution, CancellationToken cancellationToken)
    {
        var donors = new Dictionary<Guid, List<string>>();
        var locals = resolution.Options
            .Where(o => o.LocalReleaseId is not null)
            .Select(o => o.LocalReleaseId!.Value)
            .Distinct()
            .ToList();
        if (locals.Count == 0 || resolution.Options.All(o => o.Provider is null || o.LocalReleaseId is not null))
        {
            return (resolution, donors);
        }

        var works = new Dictionary<Guid, (WorkKind Kind, string Title, IReadOnlyList<string> Creators)>();
        foreach (var id in locals)
        {
            if (await db.Releases.FindAsync([id], cancellationToken) is { } release
                && await db.Works.FindAsync([release.WorkId], cancellationToken) is { } work)
            {
                works[id] = (work.Kind, work.Title, ItemProjection.Creators(work.CreatorsJson));
            }
        }

        var rank = locals.Where(works.ContainsKey).ToList(); // option order: the scorer's preference
        if (rank.Count == 0)
        {
            return (resolution, donors);
        }

        var rewritten = new List<ResolutionOption>(resolution.Options.Count);
        var collapsed = false;
        foreach (var option in resolution.Options)
        {
            Guid? target = null;
            if (option is { Provider: { } provider, LocalReleaseId: null })
            {
                foreach (var id in rank)
                {
                    if (SameNovel.IsSame(
                            works[id].Kind, works[id].Title, works[id].Creators,
                            provider.Kind, provider.Title, provider.Creators))
                    {
                        target = id;
                        break;
                    }
                }
            }

            if (option.Provider is { } donor && target is { } localId)
            {
                rewritten.Add(option with { LocalReleaseId = localId, Provider = null });
                if (donor.CoverUrl is not null)
                {
                    if (!donors.TryGetValue(localId, out var covers))
                    {
                        donors[localId] = covers = [];
                    }

                    covers.Add(donor.CoverUrl);
                }

                collapsed = true;
            }
            else
            {
                rewritten.Add(option);
            }
        }

        return collapsed ? (resolution with { Options = rewritten }, donors) : (resolution, donors);
    }

    /// <summary>
    /// Shares covers between the releases one answer ensured when they are the same novel (#514): a coverless
    /// release gains its same-novel sibling's cover — the sibling's stored asset when it has one (shared, never
    /// downloaded twice), else the sibling's cover address for the commit to fetch — and a reunited provider
    /// answer's cover address heals the release it collapsed onto. Only the cover moves: the release keeps its
    /// own provider and external id, and another author's book never donates. Saves nothing; the caller saves.
    /// </summary>
    private async Task ShareSameNovelCoversAsync(
        List<EnsuredRelease> ensured, Dictionary<Guid, List<string>> donorCovers, CancellationToken cancellationToken)
    {
        var ids = ensured.Select(e => e.Id).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var releases = new List<Release>();
        var works = new Dictionary<Guid, (WorkKind Kind, string Title, IReadOnlyList<string> Creators)>();
        foreach (var id in ids)
        {
            if (await db.Releases.FindAsync([id], cancellationToken) is { } release
                && await db.Works.FindAsync([release.WorkId], cancellationToken) is { } work)
            {
                releases.Add(release);
                works[release.Id] = (work.Kind, work.Title, ItemProjection.Creators(work.CreatorsJson));
            }
        }

        var order = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        foreach (var recipient in releases)
        {
            if (recipient.CoverAssetId is not null || CoverOutcome.CoverUrlOf(recipient.ProviderPayloadsJson) is not null)
            {
                continue;
            }

            var donor = releases
                .Where(d => d.Id != recipient.Id
                    && (d.CoverAssetId is not null || CoverOutcome.CoverUrlOf(d.ProviderPayloadsJson) is not null)
                    && SameNovel.IsSame(
                        works[recipient.Id].Kind, works[recipient.Id].Title, works[recipient.Id].Creators,
                        works[d.Id].Kind, works[d.Id].Title, works[d.Id].Creators))
                .OrderByDescending(d => d.CoverAssetId is not null)
                .ThenBy(d => order[d.Id])
                .FirstOrDefault();
            if (donor?.CoverAssetId is { } asset)
            {
                recipient.CoverAssetId = asset;
                recipient.ProviderPayloadsJson = CoverOutcome.WithOutcome(recipient.ProviderPayloadsJson, CoverOutcome.Fetched, null)
                    ?? recipient.ProviderPayloadsJson;
            }
            else if (donor is not null && CoverOutcome.CoverUrlOf(donor.ProviderPayloadsJson) is { } url)
            {
                recipient.ProviderPayloadsJson = CoverOutcome.WithCoverUrl(recipient.ProviderPayloadsJson, url);
            }
            else if (donorCovers.TryGetValue(recipient.Id, out var covers) && covers is [var cover, ..])
            {
                recipient.ProviderPayloadsJson = CoverOutcome.WithCoverUrl(recipient.ProviderPayloadsJson, cover);
            }
        }
    }

    /// <summary>
    /// The row's guidance after an answer (design §8(a)): a fresh answer recomputes it, except a reshoot signal from the
    /// phone stays until the row is picked or resolved — a re-resolve must not trade "move closer" for a vaguer default.
    /// </summary>
    private static GuidanceCode? Reguide(ScanCandidate candidate, BarcodeResolution resolution)
    {
        if (candidate.Resolution == CandidateResolution.Matched)
        {
            return null;
        }

        if (candidate.Resolution == CandidateResolution.Ambiguous)
        {
            return GuidanceCode.PickMatch;
        }

        if (candidate.GuidanceCode is { } keep && GuidanceCodes.IsReshoot(keep))
        {
            return keep;
        }

        return GuidanceCodes.For(null, candidate.Resolution, candidate.Source, candidate.RawCode, UpcQuotaWindow.IsSpent(resolution));
    }

    /// <summary>
    /// The commit tier of an answer (docs/16 §6, §16). T0 is a barcode-proof hit: a normalized identifier resolving to
    /// exactly one release the library does not already own (unless the session opted into copies). T1 is a
    /// high-confidence spine or cover: <see cref="MatchScorer"/> Matched (the 0.90 + 0.08 lead rules,
    /// re-checked on the scores kept), every contender one work, the series-volume rule clear, the library not
    /// owning it, and a spine/cover shot (a loose item is always T2, never T1). Anything else is T2, reviewed by the
    /// person. Pending has no tier yet. <paramref name="verdict"/> is the verdict after the veto. An <paramref name="unconfirmed"/> read
    /// (only the picture model saw it, where Vision found nothing) is T2 at most, however exactly the catalogue matched it: the model can
    /// read a title that happens to exist, so a person confirms it. Only the tier moves; the candidates and the verdict are as scored.
    /// </summary>
    private ScanTier? EvaluateTier(
        ResolutionVerdict verdict,
        BarcodeResolution resolution,
        List<(ResolutionOption Option, Guid ReleaseId)> taken,
        Dictionary<Guid, ReleaseFact> facts,
        int firstOwned,
        bool fromIdentifier,
        bool copiesMode,
        ShotKind shotKind,
        bool unconfirmed)
    {
        if (verdict == ResolutionVerdict.Pending)
        {
            return null;
        }

        if (unconfirmed)
        {
            return ScanTier.T2;
        }

        if (fromIdentifier && verdict == ResolutionVerdict.Matched && resolution.Options.Count == 1 && (firstOwned == 0 || copiesMode))
        {
            return ScanTier.T0;
        }

        if (!fromIdentifier
            && verdict == ResolutionVerdict.Matched
            && shotKind is ShotKind.SingleCover or ShotKind.ShelfSegment or ShotKind.Unknown
            && firstOwned == 0
            && MatchedByScores(taken)
            && SingleWork(taken, facts))
        {
            return ScanTier.T1;
        }

        return ScanTier.T2;
    }

    /// <summary>
    /// The Matched rules (0.90 + 0.08 lead) on the scores kept, best against runner-up (a lone option has no
    /// runner-up). The verdict already passed them on the full pool; this pins the tier to the same rules whatever
    /// path produced the answer.
    /// </summary>
    private bool MatchedByScores(List<(ResolutionOption Option, Guid ReleaseId)> taken)
    {
        if (taken.Count == 0)
        {
            return false;
        }

        var scores = taken.Select(t => t.Option.Score).OrderDescending().ToList();
        return MatchScorer.Classify(scores[0], scores.Count > 1 ? scores[1] : null, scoring.CurrentValue.Thresholds)
            == MatchOutcome.Matched;
    }

    /// <summary>
    /// Single-work agreement (docs/16 §6): every contender — every option at or above the Ambiguous band — is a
    /// release of one work. A lone contender trivially agrees; an option pointing nowhere known does not.
    /// </summary>
    private bool SingleWork(List<(ResolutionOption Option, Guid ReleaseId)> taken, Dictionary<Guid, ReleaseFact> facts)
    {
        var band = scoring.CurrentValue.Thresholds.AmbiguousScore;
        Guid? work = null;
        foreach (var (_, releaseId) in taken.Where(t => t.Option.Score >= band))
        {
            if (!facts.TryGetValue(releaseId, out var fact))
            {
                return false;
            }

            work ??= fact.WorkId;
            if (fact.WorkId != work)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What the tier and the veto read off an option's release: its work, and the title the veto compares.</summary>
    private sealed record ReleaseFact(Guid WorkId, string Title);

    /// <summary>
    /// The work and title of each ensured release, by release id. Reads through the change tracker first, so a
    /// release (or work) this same answer just ensured is found before it is saved.
    /// </summary>
    private async Task<Dictionary<Guid, ReleaseFact>> ReleaseFactsAsync(List<EnsuredRelease> ensured, CancellationToken cancellationToken)
    {
        var facts = new Dictionary<Guid, ReleaseFact>();
        foreach (var id in ensured.Select(e => e.Id).Distinct())
        {
            if (await db.Releases.FindAsync([id], cancellationToken) is { } release
                && await db.Works.FindAsync([release.WorkId], cancellationToken) is { } work)
            {
                facts[id] = new ReleaseFact(release.WorkId, work.Title);
            }
        }

        return facts;
    }

    /// <summary>
    /// The series-volume veto (docs/16 §6): two same-series options with different volumes inside the Ambiguous band force
    /// the person to pick, however high the winner scored. A provider option is compared by the title it answered with;
    /// a local option by its work's title. No threshold tuning overrides it; pinned by locked tests.
    /// </summary>
    private bool VetoApplies(
        BarcodeResolution resolution, List<(ResolutionOption Option, Guid ReleaseId)> taken, Dictionary<Guid, ReleaseFact> facts)
    {
        if (resolution.Verdict != ResolutionVerdict.Matched || resolution.Options.Count < 2)
        {
            return false;
        }

        var band = scoring.CurrentValue.Thresholds.AmbiguousScore;
        var titles = taken
            .Where(t => t.Option.Score >= band)
            .Select(t => t.Option.Provider?.Title ?? (facts.TryGetValue(t.ReleaseId, out var fact) ? fact.Title : null))
            .OfType<string>()
            .ToList();
        for (var i = 0; i < titles.Count; i++)
        {
            for (var j = i + 1; j < titles.Count; j++)
            {
                if (SeriesVolumes.Vetoes(titles[i], titles[j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the answer is a close call the phone should settle with artwork (<see cref="CloseCall"/>). A barcode never is:
    /// an exact code is not settled by a picture. Judged with the profile the item was scored with and the live options.
    /// </summary>
    private bool NeedsArt(ScanCandidate candidate, BarcodeResolution resolution)
    {
        if (candidate.Source == CandidateSource.Barcode)
        {
            return false;
        }

        var options = scoring.CurrentValue;
        var profile = StoredRead.From(candidate).Profile;
        return CloseCall.NeedsArt(
            resolution.Verdict,
            resolution.Options.Take(MaxAlternatives).Select(o => o.Score).ToList(),
            CloseCall.ArtWeight(options, profile),
            options.Art.CloseCallMargin,
            alreadyCompared: resolution.Steps.Any(step => step.Provider == ScoreCandidateArtwork.StepName),
            shot: candidate.ShotKind);
    }

    /// <summary>Where the library already has the release (exactly), for a release it may or may not have.</summary>
    public async Task<OwnershipDto> OwnedAsync(Guid libraryId, EnsuredRelease release, CancellationToken cancellationToken) =>
        release.IsNew ? OwnershipQuery.None : await ownership.ForReleaseAsync(libraryId, release.Id, cancellationToken);

    public static List<string> OwnedAt(OwnershipDto owned) =>
        owned.Copies.Select(c => c.LocationPath).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>What a barcode-only provisional reads until the quota drain looks it up.</summary>
    public static string QuotaProvisionalText(string digits) => $"UPC {digits} — lookup pending";

    /// <summary>Stores what the person typed or the product was called, so Fix it can start from it. The property is write-once, so this goes through EF's entry, never <c>Attach</c> (which would demote a candidate this request just added from Added to Modified, and its insert to an update).</summary>
    public void SetVisibleText(ScanCandidate candidate, string text) =>
        db.ScanCandidates.Entry(candidate).Property(c => c.VisibleText).CurrentValue = text.Length > 2000 ? text[..2000] : text;

    private static StoredAlternative AlternativeOf(
        ResolutionOption option, Guid releaseId, Dictionary<Guid, Catalog.ReleaseDto> known, int ownedCount)
    {
        if (option.Provider is { } provider)
        {
            return new StoredAlternative(
                releaseId, provider.Title, Domain.Common.EnumText.ToText(provider.Format ?? Domain.Catalog.ReleaseFormat.Unknown),
                provider.Platform, provider.Year, option.Score, provider.Creators, provider.CoverUrl, ownedCount);
        }

        return known.TryGetValue(releaseId, out var release)
            ? CandidateProjection.AlternativeOf(release, option.Score, ownedCount)
            : new StoredAlternative(releaseId, string.Empty, "unknown", null, null, option.Score, [], null, ownedCount);
    }
}
