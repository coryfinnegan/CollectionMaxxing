using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Ownership;
using CollectionMaxxing.Application.Scanning.Artwork;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Application.Scanning.Quota;
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

    /// <summary>
    /// Writes an answer onto the candidate: the resolution, the commit tier (docs/16 §6), the matched release, the top
    /// three, who already owns it, and what to do by default. Saves nothing; the caller saves.
    /// <paramref name="fromIdentifier"/> is true when the answer comes from a barcode (a normalized identifier), the only
    /// path that can reach T0. <paramref name="copiesMode"/> is true when the session opted into copies: an exact-owned
    /// hit is accepted (and T0) instead of skipped.
    /// </summary>
    public async Task ApplyAsync(
        Guid libraryId, ScanCandidate candidate, BarcodeResolution resolution, CancellationToken cancellationToken,
        bool fromIdentifier = false, bool copiesMode = false)
    {
        // The series-volume veto lands first: a vetoed match is Ambiguous however high it scored, so the person picks.
        var verdict = fromIdentifier && VetoApplies(resolution) ? ResolutionVerdict.Ambiguous : resolution.Verdict;
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

        if (verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous)
        {
            var alternatives = new List<StoredAlternative>();
            var ensured = new List<EnsuredRelease>();
            foreach (var option in resolution.Options.Take(MaxAlternatives))
            {
                ensured.Add(option.LocalReleaseId is { } local
                    ? new EnsuredRelease(local, false)
                    : await releases.EnsureAsync(option.Provider!, cancellationToken));
            }

            var known = await CandidateProjection.LoadReleasesAsync(
                db, ensured.Where(e => !e.IsNew).Select(e => e.Id).ToList(), cancellationToken);
            for (var i = 0; i < ensured.Count; i++)
            {
                var option = resolution.Options[i];
                var owned = await OwnedAsync(libraryId, ensured[i], cancellationToken);
                if (i == 0 && verdict == ResolutionVerdict.Matched)
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

                if (i == 0 && verdict == ResolutionVerdict.Ambiguous)
                {
                    candidate.Score = option.Score;
                }

                alternatives.Add(AlternativeOf(option, ensured[i].Id, known, owned.Count));
            }

            candidate.AlternativesJson = CandidateJson.Serialize(alternatives);
        }

        candidate.Tier = EvaluateTier(verdict, resolution.Options.Count, firstOwned, fromIdentifier, copiesMode);

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
        candidate.TraceJson = CandidateJson.Serialize(new StoredTrace(
            resolution.Steps, ownedAt, NeedsArt(candidate, resolution), autoDecision, retryAfter));
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
    /// The commit tier of an answer (docs/16 §6): T0 is a barcode-proof hit, a normalized identifier resolving to exactly
    /// one release that the library does not already own (unless the session opted into copies). Anything else is T2,
    /// reviewed by the person. Pending has no tier yet. (T1 lands with the spine/cover path in issue #343.)
    /// </summary>
    private static ScanTier? EvaluateTier(ResolutionVerdict verdict, int options, int firstOwned, bool fromIdentifier, bool copiesMode)
    {
        if (verdict == ResolutionVerdict.Pending)
        {
            return null;
        }

        return fromIdentifier && verdict == ResolutionVerdict.Matched && options == 1 && (firstOwned == 0 || copiesMode)
            ? ScanTier.T0
            : ScanTier.T2;
    }

    /// <summary>
    /// The series-volume veto (docs/16 §6): two same-series options with different volumes inside the Ambiguous band force
    /// the person to pick, however high the winner scored. No threshold tuning overrides it; pinned by a locked test.
    /// </summary>
    private bool VetoApplies(BarcodeResolution resolution)
    {
        if (resolution.Verdict != ResolutionVerdict.Matched || resolution.Options.Count < 2)
        {
            return false;
        }

        var band = scoring.CurrentValue.Thresholds.AmbiguousScore;
        var titles = resolution.Options
            .Where(o => o.Provider?.Title is not null && o.Score >= band)
            .Select(o => o.Provider!.Title!)
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
            alreadyCompared: resolution.Steps.Any(step => step.Provider == ScoreCandidateArtwork.StepName));
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
                releaseId, provider.Title, Domain.Common.EnumText.ToText(provider.Format ?? Domain.Catalog.ReleaseFormat.Other),
                provider.Platform, provider.Year, option.Score, provider.Creators, provider.CoverUrl, ownedCount);
        }

        return known.TryGetValue(releaseId, out var release)
            ? CandidateProjection.AlternativeOf(release, option.Score, ownedCount)
            : new StoredAlternative(releaseId, string.Empty, "other", null, null, option.Score, [], null, ownedCount);
    }
}
