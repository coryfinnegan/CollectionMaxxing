using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Events;

/// <summary>The event names of <c>GET /scan-sessions/{id}/events</c> (docs/04).</summary>
public static class ScanEventNames
{
    public const string CandidateAdded = "candidate.added";
    public const string CandidateUpdated = "candidate.updated";

    /// <summary>Defined for the picture path; nothing emits it before Wave 3.</summary>
    public const string PhotoStatus = "photo.status";
    public const string SessionStatus = "session.status";
}

/// <summary>The session's state, as sent with <c>session.status</c>.</summary>
public sealed record SessionStatusDto(Guid Id, ScanSessionStatus Status);

/// <summary>
/// One event for the wire. <see cref="Id"/> is the highest candidate <c>seq</c> the stream has shown so far (0 when none),
/// so it never goes backwards and <c>Last-Event-ID</c> says how far a reconnecting client got. <see cref="Data"/> is a
/// <see cref="CandidateDto"/> or a <see cref="SessionStatusDto"/>.
/// </summary>
public sealed record ScanEvent(string Name, int Id, object Data);

/// <summary>What one look at the session found.</summary>
public sealed record ScanEventBatch(IReadOnlyList<ScanEvent> Events, bool SessionEnded);

/// <summary>
/// The events of one session for one connected client. The database is the source of truth: each <see cref="NextAsync"/>
/// compares the session's candidates with what this client has already been shown, so a change made by any request or
/// background job (in any process) reaches the client, and nothing is lost between a slow code being Pending and resolved.
/// The first look sends every candidate the client doesn't have (everything after <c>Last-Event-ID</c> on a reconnect),
/// then the session's status. Not thread-safe: one per connection.
/// </summary>
public sealed class ScanEventCursor
{
    private readonly IAppDbContext _db;
    private readonly Guid _sessionId;
    private readonly int _after;
    private readonly Dictionary<Guid, uint> _shown = [];
    private bool _first = true;
    private int _highWater;
    private ScanSessionStatus? _statusShown;

    internal ScanEventCursor(IAppDbContext db, Guid sessionId, int after)
    {
        _db = db;
        _sessionId = sessionId;
        _after = after;
        _highWater = after;
    }

    public async Task<ScanEventBatch> NextAsync(CancellationToken cancellationToken)
    {
        var status = await _db.ScanSessions.AsNoTracking()
            .Where(s => s.Id == _sessionId).Select(s => (ScanSessionStatus?)s.Status).SingleOrDefaultAsync(cancellationToken);
        if (status is null)
        {
            return new ScanEventBatch([], true); // gone (abandoned and cleaned up)
        }

        var candidates = await _db.ScanCandidates.AsNoTracking()
            .Where(c => c.SessionId == _sessionId).OrderBy(c => c.Seq).ToListAsync(cancellationToken);

        var added = new List<ScanCandidate>();
        var updated = new List<ScanCandidate>();
        foreach (var candidate in candidates)
        {
            if (_first && candidate.Seq <= _after)
            {
                _shown[candidate.Id] = candidate.Version; // the client has it from before the reconnect
            }
            else if (!_shown.TryGetValue(candidate.Id, out var version))
            {
                added.Add(candidate);
            }
            else if (version != candidate.Version)
            {
                updated.Add(candidate);
            }
        }

        _first = false;
        var events = new List<ScanEvent>();
        var changed = added.Concat(updated).ToList();
        if (changed.Count > 0)
        {
            var dtos = (await CandidateProjection.ToDtosAsync(_db, changed, cancellationToken)).ToDictionary(d => d.Id);
            foreach (var candidate in added)
            {
                _highWater = Math.Max(_highWater, candidate.Seq);
                events.Add(new ScanEvent(ScanEventNames.CandidateAdded, _highWater, dtos[candidate.Id]));
                _shown[candidate.Id] = candidate.Version;
            }

            foreach (var candidate in updated)
            {
                events.Add(new ScanEvent(ScanEventNames.CandidateUpdated, _highWater, dtos[candidate.Id]));
                _shown[candidate.Id] = candidate.Version;
            }
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
public sealed class ScanEventFeed(IAppDbContext db, ICurrentUser currentUser)
{
    /// <returns>The stream, or null when the session isn't the caller's (the same 404 as every other scan endpoint).</returns>
    public async Task<ScanEventCursor?> OpenAsync(Guid sessionId, int? lastEventId, CancellationToken cancellationToken) =>
        await ScanSessions.FindMineAsync(db, currentUser, sessionId, cancellationToken) is { } session
            ? new ScanEventCursor(db, session.Id, Math.Max(0, lastEventId ?? 0))
            : null;
}
