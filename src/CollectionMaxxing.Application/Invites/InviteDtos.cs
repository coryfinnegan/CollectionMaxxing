using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Invites;

/// <summary>An invite as the owner sees it in the list. The token is never shown again after it is created.</summary>
public sealed record InviteDto(
    Guid Id,
    string Name,
    LibraryRole Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    Guid? AcceptedBy,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// A new invite. The link, the QR payload and the short <c>Code</c> (<c>ABCD-EFGH-JK</c>) are returned this once; only hashes of the token
/// and the code are stored.
/// </summary>
public sealed record CreatedInviteDto(InviteDto Invite, string Link, string QrPayload, string Code);

/// <summary>What the join page shows before the person signs in: who invited them, to what, and as what.</summary>
public sealed record InvitePreviewDto(string OwnerName, string LibraryName, LibraryRole Role, DateTimeOffset ExpiresAt);
