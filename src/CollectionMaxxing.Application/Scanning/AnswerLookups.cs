using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Ownership;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// What writing several answers onto candidates reads from the database about the releases they point at, read once for all of
/// them: the releases as the response shows them, whether the library owns them, and the rows themselves in the change tracker
/// (which <see cref="CandidateResolver"/> reads through <c>Find</c>). A picture of a shelf has dozens of answers; asking for each
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

    internal AnswerLookups(IAppDbContext db, OwnershipQuery ownership, Guid libraryId)
    {
        _db = db;
        _ownership = ownership;
        _libraryId = libraryId;
    }

    /// <summary>Loads all three for <paramref name="releaseIds"/> in a fixed number of queries.</summary>
    internal async Task LoadAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
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
}
