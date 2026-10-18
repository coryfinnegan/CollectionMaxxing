using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>The <c>web_sessions</c> table behind <see cref="IWebSessions"/>.</summary>
internal sealed class WebSessionStore(AppDbContext db, TimeProvider time) : IWebSessions
{
    public async Task<Guid> StartAsync(Guid userId, string label, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        // A row older than the cookie's absolute limit can never be good again; keep the table from growing for ever.
        var dead = now - IWebSessions.AbsoluteLifetime - TimeSpan.FromDays(1);
        await db.Set<WebSession>().Where(s => s.UserId == userId && s.CreatedAt < dead).ExecuteDeleteAsync(cancellationToken);

        var session = new WebSession
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Label = label.Length > WebSession.MaxLabelLength ? label[..WebSession.MaxLabelLength] : label,
            CreatedAt = now,
            LastSeenAt = now,
        };
        db.Set<WebSession>().Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    public async Task<bool> TouchAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var row = await db.Set<WebSession>().AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId)
            .Select(s => new { s.RevokedAt, s.LastSeenAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null || row.RevokedAt is not null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        if (now - row.LastSeenAt >= IWebSessions.LastSeenResolution)
        {
            await db.Set<WebSession>().Where(s => s.Id == sessionId && s.RevokedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastSeenAt, now), cancellationToken);
        }

        return true;
    }

    public async Task<IReadOnlyList<WebSessionInfo>> ListActiveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var seenSince = now - IWebSessions.IdleLifetime;
        var startedSince = now - IWebSessions.AbsoluteLifetime;
        return await db.Set<WebSession>().AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.LastSeenAt >= seenSince && s.CreatedAt >= startedSince)
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new WebSessionInfo(s.Id, s.Label, s.CreatedAt, s.LastSeenAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await db.Set<WebSession>().AnyAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken))
        {
            return false;
        }

        var now = time.GetUtcNow();
        await db.Set<WebSession>().Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now), cancellationToken);
        return true;
    }

    public async Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.Set<WebSession>().Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now), cancellationToken);
    }
}
