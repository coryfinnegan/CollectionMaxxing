using CollectionMaxxing.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// Use case: which library does this scan session belong to? Endpoints under <c>/scan-sessions/{id}</c> are not under a
/// library route, so the API asks here before it checks the caller's role (docs/07, "Layers"). The library filter hides
/// sessions of libraries the caller isn't in, so for them the answer is "none", the same as for a session that doesn't exist.
/// </summary>
public static class FindSessionLibrary
{
    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Guid?> HandleAsync(Guid sessionId, CancellationToken cancellationToken) =>
            await db.ScanSessions
                .Where(s => s.Id == sessionId)
                .Select(s => (Guid?)s.LibraryId)
                .SingleOrDefaultAsync(cancellationToken);
    }
}

/// <summary>
/// Use case: is this scan session the caller's own? A session belongs to its library and to the user who started it, so
/// another member of the same library (even the owner) gets 404 for it, never its contents.
/// </summary>
public static class IsMySession
{
    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser)
    {
        public async Task<bool> HandleAsync(Guid sessionId, CancellationToken cancellationToken) =>
            currentUser.UserId is { } userId
            && await db.ScanSessions.AnyAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);
    }
}
