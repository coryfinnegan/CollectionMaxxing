namespace CollectionMaxxing.Application.Accounts;

// Response shapes of the account, session and sign-in endpoints (docs/04, docs/14). Fields are the contract.

/// <summary>A signed-in browser. <c>Status</c> is <c>active</c>.</summary>
public sealed record SessionDto(Guid Id, string DeviceName, string Kind, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool IsCurrent, string Status);

/// <summary>An export that has been started, and where to download it once it is ready.</summary>
public sealed record ExportDto(Guid JobId, string Status, string? DownloadUrl);

/// <summary>
/// One identity the person can sign in with (Settings, Sign-in methods). <c>Provider</c> is <c>google</c>, <c>apple</c> or <c>other</c>,
/// read from the start of Auth0's <c>sub</c>; <c>IsCurrent</c> marks the identity behind this session.
/// </summary>
public sealed record SignInMethodDto(Guid Id, string Provider, string? Email, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool IsCurrent);

/// <summary>Where the browser goes next: <c>POST /setup</c> and <c>POST /account/recover</c> answer with the sign-in to start.</summary>
public sealed record NextStepDto(string Next);

/// <summary>Auth0's sign-out address. The web app navigates there after <c>POST /account/logout</c>.</summary>
public sealed record LogoutDto(string LogoutUrl);
