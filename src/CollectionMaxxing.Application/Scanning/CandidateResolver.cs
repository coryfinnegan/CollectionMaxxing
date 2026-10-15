using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Ownership;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// The database half of resolving a candidate: the catalogue's own answer to a barcode, and writing any answer onto the
/// candidate (the matched release, the top three, who already owns it, and what to do by default). Not thread-safe: use it
/// from one request at a time, and run the providers (<see cref="BarcodeResolver"/>) beside it.
/// </summary>
public sealed class CandidateResolver(IAppDbContext db, ReleaseStore releases, OwnershipQuery ownership)
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

    /// <summary>Writes an answer onto the candidate. Saves nothing; the caller saves.</summary>
    public async Task ApplyAsync(Guid libraryId, ScanCandidate candidate, BarcodeResolution resolution, CancellationToken cancellationToken)
    {
        candidate.Resolution = resolution.Verdict switch
        {
            ResolutionVerdict.Matched => CandidateResolution.Matched,
            ResolutionVerdict.Ambiguous => CandidateResolution.Ambiguous,
            ResolutionVerdict.NotFound => CandidateResolution.NotFound,
            _ => CandidateResolution.Pending,
        };
        candidate.MatchedReleaseId = null;
        candidate.Score = null;
        candidate.AlternativesJson = null;
        candidate.AlreadyOwnedCount = 0;
        candidate.Decision = CandidateDecision.Undecided;
        var ownedAt = new List<string>();

        if (resolution.Verdict is ResolutionVerdict.Matched or ResolutionVerdict.Ambiguous)
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
                if (i == 0 && resolution.Verdict == ResolutionVerdict.Matched)
                {
                    candidate.MatchedReleaseId = ensured[i].Id;
                    candidate.Score = option.Score;
                    candidate.AlreadyOwnedCount = owned.Count;
                    ownedAt = OwnedAt(owned);

                    // An exactly owned release is skipped unless the user adds another copy; anything else is added.
                    candidate.Decision = owned.Count > 0 ? CandidateDecision.Rejected : CandidateDecision.Accepted;
                }

                if (i == 0 && resolution.Verdict == ResolutionVerdict.Ambiguous)
                {
                    candidate.Score = option.Score;
                }

                alternatives.Add(AlternativeOf(option, ensured[i].Id, known, owned.Count));
            }

            candidate.AlternativesJson = CandidateJson.Serialize(alternatives);
        }

        if (resolution.ProductTitle is { } productTitle && candidate.VisibleText is null)
        {
            SetVisibleText(candidate, productTitle);
        }

        candidate.TraceJson = CandidateJson.Serialize(new StoredTrace(resolution.Steps, ownedAt));
    }

    /// <summary>Where the library already has the release (exactly), for a release it may or may not have.</summary>
    public async Task<OwnershipDto> OwnedAsync(Guid libraryId, EnsuredRelease release, CancellationToken cancellationToken) =>
        release.IsNew ? OwnershipQuery.None : await ownership.ForReleaseAsync(libraryId, release.Id, cancellationToken);

    public static List<string> OwnedAt(OwnershipDto owned) =>
        owned.Copies.Select(c => c.LocationPath).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Stores what the person typed or the product was called, so Fix it can start from it. The property is write-once, so this goes through EF.</summary>
    public void SetVisibleText(ScanCandidate candidate, string text) =>
        db.ScanCandidates.Attach(candidate).Property(c => c.VisibleText).CurrentValue = text.Length > 2000 ? text[..2000] : text;

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
