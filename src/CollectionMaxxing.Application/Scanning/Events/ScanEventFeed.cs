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
    /// A candidate's auto-add landed, T0 or T1. It companions the <c>candidate.added</c>/<c>candidate.updated</c> of the same
    /// change and carries the same <c>CandidateDto</c>; it carries no event id, so it never moves the resume cursor.
    /// Clients detect auto-adds from the candidate payload (a set <c>createdItemId</c>), which also covers polling.
    /// </summary>
    public const string CandidateAutoCommitted = "candidate.auto_committed";

    /// <summary>A candidate's uploaded photo ("Use my photo") is stored and ready.</summary>
    public const string PhotoStatus = "photo.status";
    public const string SessionStatus = "session.status";

    /// <summary>The session's live counters changed: the four bar counters plus the change counter they were counted at.</summary>
    public const string SessionCounters = "session.counters";

    /// <summary>A row was kept as a provisional record: its <c>ProvisionalDto</c>.</summary>
    public const string ProvisionalCreated = "provisional.created";

    /// <summary>A provisional record was acted on (promoted, resolved): its <c>ProvisionalDto</c>. A delete rides <c>session.counters</c>.</summary>
    public const string ProvisionalUpdated = "provisional.updated";
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
/// first asks, in one small query, for the session's status and its newest change counter, and loads candidates only when
/// that is past the highest this client has been shown. So a change made by any request or background job (in any process)
/// reaches the client, and a stream with nothing new costs that one query and reads no candidate. The counter is handed out
/// in commit order (see <see cref="ScanCandidate.ChangeSeq"/>), so a look can never skip a change that commits late. A photo
/// or a kept row is only ever changed together with the candidate that points at it, so they ride the same counter.
/// The first look sends everything after <c>Last-Event-ID</c> on a reconnect (a candidate that was only updated while the
/// client was away comes back as <c>candidate.updated</c>), then the session's status. A client who stops belonging to the
/// session's library while the stream is open is looked up again at every look (the request's own memberships were read once,
/// when it opened), and the stream ends at the next one. Not thread-safe: one per connection.
/// </summary>
public sealed class ScanEventCursor
{
    // What a look with nothing new returns, so that it allocates nothing.
    private static readonly ScanEventBatch Idle = new([], false);

    private readonly IAppDbContext _db;
    private readonly IMembershipReader _memberships;
    private readonly Guid _userId;
    private readonly Guid _libraryId;
    private readonly Guid _sessionId;
    private readonly int _after;
    private readonly HashSet<Guid> _known = [];
    private readonly Dictionary<Guid, Guid> _photosShown = [];
    private readonly Dictionary<Guid, DateTimeOffset?> _provisionalsShown = [];
    private bool _first = true;
    private int _highWater;

    // A reconnecting client was shown every candidate created at or before this position (creation order is change order).
    private int _knownSeq;
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

        var probe = await _db.ScanSessions.AsNoTracking()
            .Where(s => s.Id == _sessionId)
            .Select(s => new
            {
                s.Status,
                Latest = _db.ScanCandidates.Where(c => c.SessionId == s.Id).Max(c => (int?)c.ChangeSeq),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (probe is null)
        {
            return new ScanEventBatch([], true); // gone (abandoned and cleaned up)
        }

        var moved = _first || probe.Latest > _highWater;
        if (!moved && probe.Status == _statusShown)
        {
            return Idle;
        }

        var events = new List<ScanEvent>();
        if (moved)
        {
            await AddChangesAsync(events, cancellationToken);
        }

        if (_statusShown != probe.Status)
        {
            _statusShown = probe.Status;
            events.Add(new ScanEvent(ScanEventNames.SessionStatus, _highWater, new SessionStatusDto(_sessionId, probe.Status)));
        }

        return new ScanEventBatch(events, probe.Status != ScanSessionStatus.Open);
    }

    private async Task AddChangesAsync(List<ScanEvent> events, CancellationToken cancellationToken)
    {
        if (_first && _after > 0)
        {
            // The candidates the client has: those not changed since Last-Event-ID, and those created before the newest of
            // them, even if they have changed since.
            _knownSeq = await _db.ScanCandidates.AsNoTracking()
                .Where(c => c.SessionId == _sessionId && c.ChangeSeq <= _after)
                .MaxAsync(c => (int?)c.Seq, cancellationToken) ?? 0;
        }

        var shownUpTo = _highWater;
        var changed = await _db.ScanCandidates.AsNoTracking()
            .Where(c => c.SessionId == _sessionId && c.ChangeSeq > shownUpTo)
            .OrderBy(c => c.ChangeSeq)
            .ToListAsync(cancellationToken);
        if (changed.Count > 0)
        {
            var dtos = await CandidateProjection.ToDtosAsync(_db, changed, cancellationToken);
            for (var i = 0; i < changed.Count; i++)
            {
                var candidate = changed[i];
                var name = _known.Add(candidate.Id) && candidate.Seq > _knownSeq
                    ? ScanEventNames.CandidateAdded
                    : ScanEventNames.CandidateUpdated;
                events.Add(new ScanEvent(name, candidate.ChangeSeq, dtos[i]));
                if (candidate.AutoCommittedItemIds is { Length: > 0 })
                {
                    events.Add(new ScanEvent(ScanEventNames.CandidateAutoCommitted, 0, dtos[i]));
                }
            }

            _highWater = changed[^1].ChangeSeq;
        }

        await AddPhotoEventsAsync(events, shownUpTo, cancellationToken);
        var openProvisionals = await AddProvisionalEventsAsync(events, cancellationToken);
        _first = false;

        // The live counters: sent once on (re)connect and again whenever a candidate change moves them, so background
        // tabs and the web stay consistent without re-reading the session. Like photo.status, this is state, not a
        // candidate change: it carries the current change counter but never moves the cursor. It goes out before
        // session.status, so a client that reads through the status has the whole session state and an idle stream
        // stays heartbeats only.
        var counters = await SessionCounters.CountAsync(_db, _sessionId, openProvisionals, cancellationToken);
        if (_countersShown != counters)
        {
            _countersShown = counters;
            events.Add(new ScanEvent(ScanEventNames.SessionCounters, _highWater, new SessionCountersEventDto(counters, _highWater)));
        }
    }

    /// <summary>
    /// A photo is ready when the candidate's snap holds an asset. This is state, not a candidate change: it does not move
    /// the changeSeq cursor, so it is not replayed from Last-Event-ID. A (re)connecting client hears it once per connection
    /// (the same event carries the current changeSeq as its id), which is harmless. A photo is stored or dropped by pointing
    /// the candidate at another snap, so a candidate shown before <paramref name="shownUpTo"/> has nothing new to announce,
    /// except on the first look, which covers them all.
    /// </summary>
    private async Task AddPhotoEventsAsync(List<ScanEvent> events, int shownUpTo, CancellationToken cancellationToken)
    {
        var since = _first ? 0 : shownUpTo;
        var photos = await (
            from c in _db.ScanCandidates.AsNoTracking()
            join s in _db.ScanSnaps.AsNoTracking() on c.SnapId equals (Guid?)s.Id
            where c.SessionId == _sessionId && c.ChangeSeq > since && c.ChangeSeq <= _highWater && s.AssetId != null
            orderby c.ChangeSeq
            select new { c.Id, AssetId = s.AssetId!.Value })
            .ToListAsync(cancellationToken);
        foreach (var photo in photos)
        {
            if (!_photosShown.TryGetValue(photo.Id, out var shownAsset) || shownAsset != photo.AssetId)
            {
                _photosShown[photo.Id] = photo.AssetId;
                events.Add(new ScanEvent(ScanEventNames.PhotoStatus, _highWater, new PhotoStatusDto(photo.Id, PhotoStatusDto.Ready)));
            }
        }
    }

    /// <summary>
    /// Kept rows: like photo.status, this is state, not a candidate change — it never moves the cursor. The first look
    /// only marks what the snapshot already carries; later looks announce new and resolved records. A delete has no row
    /// left to send, so it rides the counters (the client that deleted already knows).
    /// </summary>
    /// <returns>How many of the session's kept rows still need attention.</returns>
    private async Task<int> AddProvisionalEventsAsync(List<ScanEvent> events, CancellationToken cancellationToken)
    {
        var provisionals = await _db.ProvisionalRecords.AsNoTracking()
            .Where(p => p.SessionId == _sessionId).OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        var open = 0;
        foreach (var provisional in provisionals)
        {
            if (provisional.ResolvedAt is null)
            {
                open++;
            }

            if (!_provisionalsShown.TryGetValue(provisional.Id, out var shown))
            {
                _provisionalsShown[provisional.Id] = provisional.ResolvedAt;
                if (!_first)
                {
                    events.Add(new ScanEvent(
                        provisional.ResolvedAt is null ? ScanEventNames.ProvisionalCreated : ScanEventNames.ProvisionalUpdated,
                        _highWater,
                        Provisional.ProvisionalProjection.ToDto(provisional)));
                }
            }
            else if (shown != provisional.ResolvedAt)
            {
                _provisionalsShown[provisional.Id] = provisional.ResolvedAt;
                events.Add(new ScanEvent(ScanEventNames.ProvisionalUpdated, _highWater, Provisional.ProvisionalProjection.ToDto(provisional)));
            }
        }

        return open;
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
