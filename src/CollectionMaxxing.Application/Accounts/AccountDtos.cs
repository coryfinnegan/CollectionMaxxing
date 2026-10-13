using System.Text.Json;

namespace CollectionMaxxing.Application.Accounts;

// Response shapes of the account, session and sign-in endpoints (docs/04, docs/07). Fields are the contract.

/// <summary>A signed-in device or browser.</summary>
public sealed record SessionDto(Guid Id, string DeviceName, string Kind, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool IsCurrent);

/// <summary>A new device pairing: the payload of the QR code and its 6-digit fallback, valid for two minutes, single use.</summary>
public sealed record PairDeviceDto(string ServerUrl, string Token, string Code, DateTimeOffset ExpiresAt);

/// <summary>An export that has been started, and where to download it once it is ready.</summary>
public sealed record ExportDto(Guid JobId, string Status, string? DownloadUrl);

/// <summary>WebAuthn options to hand to the browser's passkey API.</summary>
public sealed record PasskeyOptionsDto(JsonElement Options);

/// <summary>The result of signing in or finishing setup.</summary>
public sealed record SignedInDto(Guid UserId, string DisplayName, Guid? LibraryId);
