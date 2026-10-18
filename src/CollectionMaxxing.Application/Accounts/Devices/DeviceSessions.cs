using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>Use case: where the user is signed in, paired phones and browsers (docs/04, <c>GET /me/sessions</c>).</summary>
public static class ListSessions
{
    /// <summary>A revoked device stays on the list this long, so the person can see that it happened.</summary>
    public static readonly TimeSpan RevokedStaysFor = TimeSpan.FromDays(30);

    public const string Active = "active";
    public const string Revoked = "revoked";
    public const string DeviceKind = "device";
    public const string WebKind = "web";

    public sealed class Handler(IDeviceAuthorizations devices, IWebSessions webSessions, TimeProvider time)
    {
        /// <param name="userId">Whose devices and browsers.</param>
        /// <param name="currentSessionId">The device or browser the request came from, if it came from one, so that it can be marked as the current session.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public async Task<Page<SessionDto>> HandleAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken)
        {
            var cutoff = time.GetUtcNow() - RevokedStaysFor;
            var phones = (await devices.ListAsync(userId, cancellationToken))
                .Where(s => !s.IsRevoked || (s.LastSeenAt ?? s.CreatedAt) >= cutoff)
                .Select(s => (
                    Seen: s.LastSeenAt ?? s.CreatedAt,
                    Dto: new SessionDto(s.Id, s.DeviceName, DeviceKind, s.CreatedAt, s.LastSeenAt, s.Id == currentSessionId, s.IsRevoked ? Revoked : Active)));

            // Browsers that were signed out simply disappear: they are not worth a row each time someone signs out.
            var browsers = (await webSessions.ListActiveAsync(userId, cancellationToken))
                .Select(w => (
                    Seen: w.LastSeenAt,
                    Dto: new SessionDto(w.Id, w.Label, WebKind, w.CreatedAt, w.LastSeenAt, w.Id == currentSessionId, Active)));

            var items = phones.Concat(browsers).OrderByDescending(s => s.Seen).Select(s => s.Dto).ToList();
            return new Page<SessionDto>(items);
        }
    }
}

/// <summary>Use case: sign one device or browser out. Its tokens or cookie stop working at once (docs/04, <c>DELETE /me/sessions/{id}</c>).</summary>
public static class RevokeSession
{
    public sealed class Handler(IDeviceAuthorizations devices, IWebSessions webSessions)
    {
        /// <summary>Someone else's session, or one that doesn't exist, is the same "not found". Revoking one twice is fine.</summary>
        public async Task<Result> HandleAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
            await devices.RevokeAsync(userId, sessionId, cancellationToken) || await webSessions.RevokeAsync(userId, sessionId, cancellationToken)
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
