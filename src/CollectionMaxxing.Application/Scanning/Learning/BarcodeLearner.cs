using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Learning;

/// <summary>
/// The learning loop's first half (docs/02, section 8): a barcode the person confirmed is saved on the release they
/// confirmed it for, so the next scan of it is a local hit and asks no provider. Only a committed, accepted candidate
/// teaches anything: a rejected or abandoned one never creates a mapping. The mapping lives on the release, so a release
/// the library made itself (private to it) keeps the mapping private too, and a global release shares it. Each link remembers the
/// session whose commit taught it, so undoing that commit (SCAN-1e) can take back exactly what it taught and nothing else.
/// </summary>
public sealed class BarcodeLearner(IAppDbContext db)
{
    /// <summary>
    /// Adds the barcodes of the accepted candidates that carry one (a barcode scan, or a row a barcode was bound
    /// to in Review) to their releases, marked as learned by <paramref name="sessionId"/>'s commit. A link the
    /// release already has (from a provider, or an earlier commit) is left as it is. Saves nothing; the caller saves.
    /// </summary>
    public async Task LearnAsync(Guid sessionId, IReadOnlyCollection<ScanCandidate> accepted, CancellationToken cancellationToken)
    {
        var wanted = new List<(Guid ReleaseId, Identifier Id)>();
        foreach (var candidate in accepted)
        {
            if (candidate is { MatchedReleaseId: { } releaseId }
                && Identifier.TryParse(candidate.RawCode, out var id)
                && !wanted.Any(w => w.ReleaseId == releaseId && w.Id.Value == id.Value))
            {
                wanted.Add((releaseId, id));
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        var releaseIds = wanted.Select(w => w.ReleaseId).Distinct().ToList();
        var values = wanted.Select(w => w.Id.Value).Distinct().ToList();
        var known = (await db.ReleaseIdentifiers.AsNoTracking()
                .Where(i => releaseIds.Contains(i.ReleaseId) && values.Contains(i.Value))
                .Select(i => new { i.ReleaseId, i.Value })
                .ToListAsync(cancellationToken))
            .Select(i => (i.ReleaseId, i.Value))
            .ToHashSet();

        foreach (var (releaseId, id) in wanted)
        {
            // A link the release already has is left as it is, whoever taught it: one already saved, or one ensured in
            // this unit of work and not saved yet (an auto-add learns right after resolving).
            if (!known.Contains((releaseId, id.Value))
                && !db.ReleaseIdentifiers.Local.Any(i => i.ReleaseId == releaseId && i.Value == id.Value))
            {
                db.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = releaseId, Type = id.Type, Value = id.Value, LearnedBySessionId = sessionId });
                known.Add((releaseId, id.Value));
            }
        }
    }

    /// <summary>
    /// A barcode the person confirmed outside a scan session (an edition upgrade, docs/16 §11) is saved on the release
    /// they confirmed it for, with no session to undo it by. A link the release already has (from a provider, or an
    /// earlier confirmation) is left as it is. Saves nothing; the caller saves.
    /// </summary>
    public async Task LearnAsync(Guid releaseId, Identifier identifier, CancellationToken cancellationToken)
    {
        var known = await db.ReleaseIdentifiers.AsNoTracking()
            .AnyAsync(i => i.ReleaseId == releaseId && i.Value == identifier.Value, cancellationToken);
        if (!known && !db.ReleaseIdentifiers.Local.Any(i => i.ReleaseId == releaseId && i.Value == identifier.Value))
        {
            db.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = releaseId, Type = identifier.Type, Value = identifier.Value });
        }
    }
}
