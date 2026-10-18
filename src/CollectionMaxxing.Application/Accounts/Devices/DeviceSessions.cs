using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>Use case: the user's paired phones (docs/04, <c>GET /me/sessions</c>).</summary>
public static class ListSessions
{
    /// <summary>A revoked device stays on the list this long, so the person can see that it happened.</summary>
    public static readonly TimeSpan RevokedStaysFor = TimeSpan.FromDays(30);

    public const string Active = "active";
    public const string Revoked = "revoked";
    public const string DeviceKind = "device";

    public sealed class Handler(IDeviceAuthorizations devices, TimeProvider time)
    {
        /// <param name="userId">Whose devices.</param>
        /// <param name="currentSessionId">The device the request came from, if it came from one, so that it can be marked as the current session.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        public async Task<Page<SessionDto>> HandleAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken)
        {
            var cutoff = time.GetUtcNow() - RevokedStaysFor;
            var sessions = await devices.ListAsync(userId, cancellationToken);
            var items = sessions
                .Where(s => !s.IsRevoked || (s.LastSeenAt ?? s.CreatedAt) >= cutoff)
                .OrderByDescending(s => s.LastSeenAt ?? s.CreatedAt)
                .Select(s => new SessionDto(
                    s.Id,
                    s.DeviceName,
                    DeviceKind,
                    s.CreatedAt,
                    s.LastSeenAt,
                    s.Id == currentSessionId,
                    s.IsRevoked ? Revoked : Active))
                .ToList();
            return new Page<SessionDto>(items);
        }
    }
}

/// <summary>Use case: sign one device out. Its tokens stop working at once (docs/04, <c>DELETE /me/sessions/{id}</c>).</summary>
public static class RevokeSession
{
    public sealed class Handler(IDeviceAuthorizations devices)
    {
        /// <summary>Someone else's device, or one that doesn't exist, is the same "not found". Revoking one twice is fine.</summary>
        public async Task<Result> HandleAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
            await devices.RevokeAsync(userId, sessionId, cancellationToken)
                ? Result.Success()
                : Result.Failure(PairingErrors.SessionNotFound);
    }
}
