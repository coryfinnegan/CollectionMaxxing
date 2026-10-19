namespace CollectionMaxxing.Application.Accounts.Devices;

/// <summary>A signed-in browser as the server knows it.</summary>
public sealed record WebSessionInfo(Guid Id, string Label, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);

/// <summary>
/// The server's record of signed-in browsers (docs/07, "Web: passkeys + same-origin cookie"). The session cookie names a record; a
/// cookie whose record is revoked, missing or somebody else's is refused. Implemented by Infrastructure.
/// </summary>
public interface IWebSessions
{
    /// <summary>How long a browser may go unseen before it drops off the list (the cookie's own idle limit).</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(8);

    /// <summary>The cookie's absolute limit: a session older than this is dead whatever its cookie says.</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    /// <summary>"Last seen" is written at most this often per session, so a request does not usually write.</summary>
    public static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    /// <summary>Records a new sign-in and returns its session id for the cookie. Also forgets this person's long-dead rows.</summary>
    Task<Guid> StartAsync(Guid userId, string label, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the cookie's session is still good: the record exists, is this user's and isn't revoked. Records "last seen"
    /// (at most once per <see cref="LastSeenResolution"/>).
    /// </summary>
    Task<bool> TouchAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken);

    /// <summary>The user's browsers that could still be signed in, most recently seen first.</summary>
    Task<IReadOnlyList<WebSessionInfo>> ListActiveAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Signs one browser out. False if the session doesn't exist or belongs to someone else. Revoking one twice is fine.</summary>
    Task<bool> RevokeAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Signs every browser of the user out. Doing it for a user with none is fine.</summary>
    Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken);
}
