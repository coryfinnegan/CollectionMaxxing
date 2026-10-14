namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>The address the phone should talk to, from configuration. Never taken from a request's Host header.</summary>
public interface IPublicServerUrl
{
    /// <summary>For example <c>https://pc.tailnet.ts.net</c>, with no trailing slash.</summary>
    string Value { get; }
}

/// <summary>One paired phone (or, later, another kind of session) as the server knows it.</summary>
public sealed record DeviceSession(Guid Id, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool IsRevoked);

/// <summary>
/// The OpenIddict side of device pairing: one authorization per paired phone, which its tokens hang from. Implemented by
/// Infrastructure, so the use cases never see the token library.
/// </summary>
public interface IDeviceAuthorizations
{
    /// <summary>Makes the authorization for a newly paired phone, or returns null if the user is gone or deactivated.</summary>
    Task<Guid?> CreateAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Whether the user may still hold tokens (they exist and haven't been deleted).</summary>
    Task<bool> UserIsActiveAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The user's paired phones, valid or revoked, with the name each one gave when it paired.</summary>
    Task<IReadOnlyList<DeviceSession>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes the phone's authorization and every token of it, at once. Returns false if the authorization doesn't exist or
    /// belongs to someone else. Revoking one that is already revoked is fine.
    /// </summary>
    Task<bool> RevokeAsync(Guid userId, Guid authorizationId, CancellationToken cancellationToken);
}
