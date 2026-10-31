using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Ownership;
using CollectionMaxxing.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// What writing several answers onto candidates, and adding the ones that qualify, reads from the database about the releases
/// they point at, read once for all of them: the releases as the response shows them, whether the library owns them, the rows
/// themselves in the change tracker (which <see cref="CandidateResolver"/> reads through <c>Find</c>) and, for an add, the
/// releases as stored with their works' kinds and the platforms. A picture of a shelf has dozens of answers; asking for each
/// answer's releases on its own costs a round trip per answer for every one of these. A release not loaded up front is loaded
/// the first time an answer asks for it, so the result is the same whatever was loaded early. One instance serves one library
/// and one request; it is not thread-safe.
/// </summary>
public sealed class AnswerLookups
{
    private readonly IAppDbContext _db;
    private readonly OwnershipQuery _ownership;
    private readonly Guid _libraryId;

    private readonly Dictionary<Guid, ReleaseDto> _known = [];
    private readonly HashSet<Guid> _knownAsked = [];
    private readonly Dictionary<Guid, OwnershipDto> _owned = [];
    private readonly HashSet<Guid> _ownedAsked = [];
    private readonly HashSet<Guid> _trackedAsked = [];
    private readonly HashSet<Guid> _expected = [];
    private readonly Dictionary<Guid, Release> _rows = [];
    private readonly HashSet<Guid> _rowsAsked = [];
    private readonly Dictionary<Guid, WorkKind> _kinds = [];
    private readonly HashSet<Guid> _kindsAsked = [];
    private List<Platform>? _platforms;

    internal AnswerLookups(IAppDbContext db, OwnershipQuery ownership, Guid libraryId)
    {
        _db = db;
        _ownership = ownership;
        _libraryId = libraryId;
    }

    /// <summary>Reads the tracker rows, the response shapes and the ownership of <paramref name="releaseIds"/> in a fixed number of queries.</summary>
    internal async Task LoadAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        _expected.UnionWith(releaseIds);
        await TrackAsync(releaseIds, cancellationToken);
        await KnownAsync(releaseIds, cancellationToken);
        await OwnedAsync(releaseIds, cancellationToken);
    }

    /// <summary>
    /// The releases the library may see among <paramref name="releaseIds"/>, as the response shows them; one that is hidden or
    /// missing is absent. Read from the database, so a release added to the change tracker and not yet saved is never among them.
    /// </summary>
    internal async Task<Dictionary<Guid, ReleaseDto>> KnownAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        var missing = releaseIds.Where(_knownAsked.Add).ToList();
        if (missing.Count > 0)
        {
            foreach (var (id, release) in await CandidateProjection.LoadReleasesAsync(_db, [_libraryId], missing, cancellationToken))
            {
                _known[id] = release;
            }
        }

        return releaseIds.Where(_known.ContainsKey).Distinct().ToDictionary(id => id, id => _known[id]);
    }

    /// <summary>Whether, and where, the library owns each of <paramref name="releaseIds"/> (<see cref="OwnershipQuery.None"/> for a release it cannot see).</summary>
    internal async Task<Dictionary<Guid, OwnershipDto>> OwnedAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        var missing = releaseIds.Where(_ownedAsked.Add).ToList();
        if (missing.Count > 0)
        {
            foreach (var (id, owned) in await _ownership.ForEachReleaseAsync(_libraryId, missing, cancellationToken))
            {
                _owned[id] = owned;
            }
        }

        return releaseIds.Distinct().ToDictionary(id => id, id => _owned[id]);
    }

    /// <summary>
    /// Brings the releases, and their works, into the change tracker, where <c>Find</c> answers from it without a query. The
    /// rows are the ones <c>Find</c> would load one at a time, so what the tracker holds is the same either way.
    /// </summary>
    internal async Task TrackAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        var missing = releaseIds.Where(_trackedAsked.Add).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var releases = await _db.Releases.Where(r => missing.Contains(r.Id)).ToListAsync(cancellationToken);
        var workIds = releases.Select(r => r.WorkId).Distinct().ToList();
        if (workIds.Count > 0)
        {
            await _db.Works.Where(w => workIds.Contains(w.Id)).ToListAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The releases among <paramref name="releaseIds"/> that the library may see, as the database stores them (not the tracker's
    /// copy). The first call reads every release the answers named along with the ones asked for, since an add that needs one
    /// nearly always needs the rest; a caller that adds nothing never causes the read.
    /// </summary>
    internal async Task<Dictionary<Guid, Release>> RowsAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        var missing = releaseIds.Where(id => !_rowsAsked.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            var toLoad = missing.Concat(_expected.Where(id => !_rowsAsked.Contains(id))).Distinct().ToList();
            foreach (var row in await _db.Releases.AsNoTracking().Where(r => toLoad.Contains(r.Id)).ToListAsync(cancellationToken))
            {
                _rows[row.Id] = row;
            }

            _rowsAsked.UnionWith(toLoad);
        }

        return releaseIds.Where(_rows.ContainsKey).Distinct().ToDictionary(id => id, id => _rows[id]);
    }

    /// <summary>The kind of each of <paramref name="workIds"/>; the first call also reads the kinds of the works of every release read so far.</summary>
    internal async Task<Dictionary<Guid, WorkKind>> KindsAsync(IReadOnlyCollection<Guid> workIds, CancellationToken cancellationToken)
    {
        var missing = workIds.Where(id => !_kindsAsked.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            var toLoad = missing.Concat(_rows.Values.Select(r => r.WorkId).Where(id => !_kindsAsked.Contains(id))).Distinct().ToList();
            foreach (var work in await _db.Works.AsNoTracking().Where(w => toLoad.Contains(w.Id)).Select(w => new { w.Id, w.Kind }).ToListAsync(cancellationToken))
            {
                _kinds[work.Id] = work.Kind;
            }

            _kindsAsked.UnionWith(toLoad);
        }

        return workIds.Where(_kinds.ContainsKey).Distinct().ToDictionary(id => id, id => _kinds[id]);
    }

    /// <summary>Every platform, read once.</summary>
    internal async Task<List<Platform>> PlatformsAsync(CancellationToken cancellationToken) =>
        _platforms ??= await _db.Platforms.AsNoTracking().ToListAsync(cancellationToken);

    /// <summary>
    /// Of <paramref name="releaseIds"/>, the ones whose stored row may lack a cover that its provider named: those with no cover
    /// asset and a provider payload, and any whose row was not read (so the database has to be asked). A release this says
    /// nothing about has a cover or has none to fetch, and a query for releases like that finds nothing.
    /// </summary>
    internal async Task<List<Guid>> MayLackCoverAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(releaseIds, cancellationToken);
        return [.. releaseIds.Where(id => !rows.TryGetValue(id, out var row) || (row.CoverAssetId is null && row.ProviderPayloadsJson is not null))];
    }
}
