using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Artwork;

/// <summary>
/// Where a close call is still open, and the artwork the phone may fetch for it. The URL is this server's own, authorized
/// cover route (<c>/media/covers/{assetId}</c>), never a provider's address: the phone never fetches from a third party.
/// </summary>
internal static class ArtworkLinks
{
    /// <summary>The server marked this item as a close call, and nobody (the person, or the phone's own artwork) has settled it since.</summary>
    public static bool AsksForArt(ScanCandidate candidate, StoredTrace trace) =>
        trace.NeedsArt && !IsDecidedByPerson(candidate, CandidateJson.ReadAlternatives(candidate.AlternativesJson), trace);

    /// <summary>
    /// The person made a decision about this item. The server decides too: a Matched item is accepted (or, when the library owns
    /// it already, rejected) by itself. That decision is the server's own for as long as the candidate is still the one it
    /// made it about and nothing changed it; a decision by the person, or a pick of another release, is theirs.
    /// </summary>
    public static bool IsDecidedByPerson(ScanCandidate candidate, IReadOnlyList<StoredAlternative> alternatives, StoredTrace trace)
    {
        if (candidate.Decision == CandidateDecision.Undecided)
        {
            return false;
        }

        var stillTheServersPick = trace.AutoDecision is { } auto
            && candidate.Decision == auto
            && alternatives.Count > 0
            && candidate.MatchedReleaseId == alternatives[0].ReleaseId;
        return !stillTheServersPick;
    }

    /// <summary>The artwork URL of each alternative of every open close call, for the releases the caller may see. One without a cover has none.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> ForCloseCallsAsync(
        IAppDbContext db, IReadOnlyList<ScanCandidate> candidates, CancellationToken cancellationToken)
    {
        var releaseIds = new HashSet<Guid>();
        foreach (var candidate in candidates)
        {
            var trace = CandidateJson.ReadTrace(candidate.TraceJson);
            if (AsksForArt(candidate, trace))
            {
                releaseIds.UnionWith(CandidateJson.ReadAlternatives(candidate.AlternativesJson).Select(a => a.ReleaseId));
            }
        }

        return await ForAsync(db, releaseIds, cancellationToken);
    }

    /// <summary>
    /// The cover URL of each release the caller may see, when the cover asset is one the caller may see too (a private asset
    /// of another library is invisible through the query filter, even when a visible release points at it).
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> ForAsync(
        IAppDbContext db, IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        if (releaseIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var rows = await (from r in db.Releases.AsNoTracking()
                          join w in db.Works.AsNoTracking() on r.WorkId equals w.Id
                          where releaseIds.Contains(r.Id)
                          select new { r.Id, ReleaseCover = r.CoverAssetId, WorkCover = w.CoverAssetId }).ToListAsync(cancellationToken);
        var assetIds = rows.SelectMany(x => new[] { x.ReleaseCover, x.WorkCover }).OfType<Guid>().Distinct().ToList();
        var visible = assetIds.Count == 0
            ? []
            : (await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id)).Select(a => a.Id).ToListAsync(cancellationToken)).ToHashSet();

        var links = new Dictionary<Guid, string>();
        foreach (var row in rows)
        {
            // The release's own cover first, then its work's, as the artwork listing does.
            if (new[] { row.ReleaseCover, row.WorkCover }.OfType<Guid>().FirstOrDefault(visible.Contains) is var asset && asset != Guid.Empty)
            {
                links[row.Id] = CatalogProjection.CoverUrl(asset);
            }
        }

        return links;
    }
}
