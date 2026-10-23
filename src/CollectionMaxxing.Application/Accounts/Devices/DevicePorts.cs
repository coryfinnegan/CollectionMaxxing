namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>The address people and phones use to reach the server, from configuration. Never taken from a request's Host header.</summary>
public interface IPublicServerUrl
{
    /// <summary>For example <c>https://pc.tailnet.ts.net</c>, with no trailing slash.</summary>
    string Value { get; }
}

/// <summary>One signed-in phone as the server knows it.</summary>
public sealed record DeviceSession(Guid Id, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool IsRevoked);

/// <summary>
/// The user's signed-in phones. Phones sign in with Auth0 now (ADR-025); registering and revoking them is AUTH-2 (#229), so until then
/// a user has none. The members list and account deletion already go through this port.
/// </summary>
public interface IDeviceAuthorizations
{
    /// <summary>The user's phones, valid or revoked.</summary>
    Task<IReadOnlyList<DeviceSession>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Revokes every phone of the user (a member removed from the library, a deleted account). A user with none is fine.</summary>
    Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken);
}
