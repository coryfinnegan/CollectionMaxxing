using CollectionMaxxing.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Audit;

/// <summary>Use case: when an account is purged, its audit events stay but no longer say who did them (used by account deletion).</summary>
public static class AnonymiseAuditActor
{
    public sealed class Handler(IAppDbContext db)
    {
        /// <summary>Clears the actor on every event of <paramref name="userId"/>; returns how many events changed.</summary>
        public Task<int> HandleAsync(Guid userId, CancellationToken cancellationToken) =>
            db.AuditEvents
                .Where(e => e.ActorUserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ActorUserId, (Guid?)null), cancellationToken);
    }
}
