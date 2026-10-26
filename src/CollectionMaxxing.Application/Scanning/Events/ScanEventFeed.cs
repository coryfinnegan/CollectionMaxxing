using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Events;

/// <summary>The event names of <c>GET /scan-sessions/{id}/events</c> (docs/04).</summary>
public static class ScanEventNames
{
    public const string CandidateAdded = "candidate.added";
    public const string CandidateUpdated = "candidate.updated";

    /// <summary>
    /// A candidate's T0 auto-add landed. It companions the <c>candidate.added</c>/<c>candidate.updated</c> of the same
    /// change and carries the same <c>CandidateDto</c>; it carries no event id, so it never moves the resume cursor.
    /// Clients detect auto-adds from the candidate payload (a set <c>createdItemId</c>), which also covers polling.
    /// </summary>
    public const string CandidateAutoCommitted = "candidate.auto_committed";

    /// <summary>A candidate's uploaded photo ("Use my photo") is stored and ready.</summary>
    public const string PhotoStatus = "photo.status";
    public const string SessionStatus = "session.status";

    /// <summary>The session's live counters changed: the four bar counters plus the change counter they were counted at.</summary>
    public const string SessionCounters = "session.counters";
}

/// <summary>The session's state, as sent with <c>session.status</c>.</summary>
public sealed record SessionStatusDto(Guid Id, ScanSessionStatus Status);

/// <summary>What <c>session.counters</c> says: the live counters, counted at <see cref="ChangeSeq"/>.</summary>
public sealed record SessionCountersEventDto(SessionCountersDto Counters, int ChangeSeq);

/// <summary>What <c>photo.status</c> says: the photo uploaded for <see cref="CandidateId"/> is <c>ready</c> (stored; it becomes the item's cover on commit).</summary>
public sealed record PhotoStatusDto(Guid CandidateId, string Status)
{
    public const string Ready = "ready";
}

/// <summary>
/// One event for the wire. <see cref="Id"/> is the highest candidate <c>changeSeq</c> the stream has shown so far (0 when
/// none): the session's change counter, which goes up with every insert and update of a candidate, so it never goes backwards
/// and <c>Last-Event-ID</c> says exactly how far a reconnecting client got. A companion event (no cursor of its own) uses 0
/// and is written without an <c>id:</c> line. <see cref="Data"/> is a <see cref="CandidateDto"/>, a
/// <see cref="SessionStatusDto"/>, or one of the other payloads above.
/// </summary>
public sealed record ScanEvent(string Name, int Id, object Data);

/// <summary>What one look at the session found.</summary>
public sealed record ScanEventBatch(IReadOnlyList<ScanEvent> Events, bool SessionEnded);

/// <summary>
/// The events of one session for one connected client. The database is the source of truth: each <see cref="NextAsync"/>
/// looks for candidates whose change counter is past the highest this client has been shown, so a change made by any request
/// or background job (in any process) reaches the client, and nothing is lost between a slow code being Pending and resolved.
/// The counter is handed out in commit order (see <see cref="ScanCandidate.ChangeSeq"/>), so a look can never skip a change
/// that commits late. The first look sends everything after <c>Last-Event-ID</c> on a reconnect (a candidate that was only
/// updated while the client was away comes back as <c>candidate.updated</c>), then the session's status. A client who stops belonging to
/// the session's library while the stream is open is looked up again at every look (the request's own memberships were read once, when
/// it opened), and the stream ends at the next one. Not thread-safe: one per connection.
/// </summary>
public sealed class ScanEventCursor
{
    private readonly IAppDbContext _db;
    private readonly IMembershipReader _memberships;
    private readonly Guid _userId;
    private readonly Guid _libraryId;
    private readonly Guid _sessionId;
    private readonly int _after;
    private readonly HashSet<Guid> _known = [];
    private readonly Dictionary<Guid, Guid> _photosShown = [];
    private bool _first = true;
    private int _highWater;
    private ScanSessionStatus? _statusShown;
    private SessionCountersDto? _countersShown;

    internal ScanEventCursor(IAppDbContext db, IMembershipReader memberships, Guid userId, Guid libraryId, Guid sessionId, int after)
    {
        _db = db;
        _memberships = memberships;
        _userId = userId;
        _libraryId = libraryId;
        _sessionId = sessionId;
        _after = after;
        _highWater = after;
    }

    public async Task<ScanEventBatch> NextAsync(CancellationToken cancellationToken)
    {
        // Left or removed since the last look: no more events, however long the stream was going to run.
        var memberships = await _memberships.GetMembershipsAsync(_userId, cancellationToken);
        if (memberships.All(m => m.LibraryId != _libraryId))
        {
            return new ScanEventBatch([], true);
        }

        var status = await _db.ScanSessions.AsNoTracking()
            .Where(s => s.Id == _sessionId).Select(s => (ScanSessionStatus?)s.Status).SingleOrDefaultAsync(cancellationToken);
        if (status is null)
        {
            return new ScanEventBatch([], true); // gone (abandoned and cleaned up)
        }

        var candidates = await _db.ScanCandidates.AsNoTracking()
            .Where(c => c.SessionId == _sessionId).OrderBy(c => c.ChangeSeq).ToListAsync(cancellationToken);

        if (_first)
        {
            // A reconnecting client has every candidate it was shown before it left: those not changed since Last-Event-ID, and
            // those created before the newest of them (creation order is change order), even if they have changed since.
            var knownSeq = candidates.Where(c => c.ChangeSeq <= _after).Select(c => c.Seq).DefaultIfEmpty(0).Max();
            foreach (var candidate in candidates.Where(c => c.ChangeSeq <= _after || c.Seq <= knownSeq))
            {
                _known.Add(candidate.Id);
            }

            _first = false;
        }

        var events = new List<ScanEvent>();
        var changed = candidates.Where(c => c.ChangeSeq > _highWater).ToList();
        if (changed.Count > 0)
        {
            var dtos = (await CandidateProjection.ToDtosAsync(_db, changed, cancellationToken)).ToDictionary(d => d.Id);
            foreach (var candidate in changed)
            {
                _highWater = Math.Max(_highWater, candidate.ChangeSeq);
                var name = _known.Add(candidate.Id) ? ScanEventNames.CandidateAdded : ScanEventNames.CandidateUpdated;
                events.Add(new ScanEvent(name, candidate.ChangeSeq, dtos[candidate.Id]));
                if (candidate.AutoCommittedItemIds is { Length: > 0 })
                {
                    events.Add(new ScanEvent(ScanEventNames.CandidateAutoCommitted, 0, dtos[candidate.Id]));
                }
            }
        }

        // A photo is ready when the candidate's snap holds an asset. This is state, not a candidate change: it does not move
        // the changeSeq cursor, so it is not replayed from Last-Event-ID. A (re)connecting client hears it once per connection
        // (the same event carries the current changeSeq as its id), which is harmless.
        var photoAssets = await _db.ScanSnaps.AsNoTracking()
            .Where(s => s.SessionId == _sessionId && s.AssetId != null)
            .Select(s => new { s.Id, AssetId = s.AssetId!.Value })
            .ToDictionaryAsync(s => s.Id, s => s.AssetId, cancellationToken);
        foreach (var candidate in candidates)
        {
            if (candidate.SnapId is { } snapId
                && photoAssets.TryGetValue(snapId, out var assetId)
                && (!_photosShown.TryGetValue(candidate.Id, out var shownAsset) || shownAsset != assetId))
            {
                _photosShown[candidate.Id] = assetId;
                events.Add(new ScanEvent(ScanEventNames.PhotoStatus, _highWater, new PhotoStatusDto(candidate.Id, PhotoStatusDto.Ready)));
            }
        }

        // The live counters: sent once on (re)connect and again whenever a candidate change moves them, so background
        // tabs and the web stay consistent without re-reading the session. Like photo.status, this is state, not a
        // candidate change: it carries the current change counter but never moves the cursor. It goes out before
        // session.status, so a client that reads through the status has the whole session state and an idle stream
        // stays heartbeats only.
        var counters = SessionCounters.Count(candidates);
        if (_countersShown != counters)
        {
            _countersShown = counters;
            events.Add(new ScanEvent(ScanEventNames.SessionCounters, _highWater, new SessionCountersEventDto(counters, _highWater)));
        }

        if (_statusShown != status)
        {
            _statusShown = status;
            events.Add(new ScanEvent(ScanEventNames.SessionStatus, _highWater, new SessionStatusDto(_sessionId, status.Value)));
        }

        return new ScanEventBatch(events, status != ScanSessionStatus.Open);
    }
}

/// <summary>Use case: open the event stream of the caller's own scan session.</summary>
public sealed class ScanEventFeed(IAppDbContext db, ICurrentUser currentUser, IMembershipReader memberships)
{
    /// <returns>The stream, or null when the session isn't the caller's (the same 404 as every other scan endpoint).</returns>
    public async Task<ScanEventCursor?> OpenAsync(Guid sessionId, int? lastEventId, CancellationToken cancellationToken) =>
        await ScanSessions.FindMineAsync(db, currentUser, sessionId, cancellationToken) is { } session
            ? new ScanEventCursor(db, memberships, session.UserId, session.LibraryId, session.Id, Math.Max(0, lastEventId ?? 0))
            : null;
}
