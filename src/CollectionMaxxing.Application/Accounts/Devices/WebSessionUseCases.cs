using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>Use case: the browsers the user is signed in on (docs/07, "Signed-in devices and browsers"). Signed-out browsers are not listed.</summary>
public static class ListWebSessions
{
    public const string Kind = "web";

    public sealed class Handler(IWebSessions webSessions)
    {
        /// <param name="userId">Whose browsers.</param>
        /// <param name="currentSessionId">The browser the request came from, so that it can be marked as the current session.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public async Task<Page<SessionDto>> HandleAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken)
        {
            var browsers = await webSessions.ListActiveAsync(userId, cancellationToken);
            return new Page<SessionDto>([.. browsers.Select(w =>
                new SessionDto(w.Id, w.Label, Kind, w.CreatedAt, w.LastSeenAt, w.Id == currentSessionId, ListSessions.Active))]);
        }
    }
}

/// <summary>Use case: sign one browser out. Its cookie stops working at once.</summary>
public static class RevokeWebSession
{
    public sealed class Handler(IWebSessions webSessions)
    {
        /// <summary>Someone else's browser, or one that doesn't exist, is the same "not found". Revoking one twice is fine.</summary>
        public async Task<Result> HandleAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
            await webSessions.RevokeAsync(userId, sessionId, cancellationToken)
                ? Result.Success()
                : Result.Failure(PairingErrors.SessionNotFound);
    }
}

/// <summary>Use case: "sign out everywhere" on the web. Every browser of the user is signed out, the one asking too. Phones keep their own sign-in.</summary>
public static class RevokeWebSessions
{
    public sealed class Handler(IWebSessions webSessions)
    {
        public Task HandleAsync(Guid userId, CancellationToken cancellationToken) => webSessions.RevokeAllAsync(userId, cancellationToken);
    }
}
