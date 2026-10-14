using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Libraries;

// Response shapes of the libraries and members endpoints (docs/04). Fields are the contract: change them deliberately.

/// <summary>A library the caller belongs to, and their role in it.</summary>
public sealed record LibrarySummaryDto(Guid Id, string Name, LibraryRole Role);

public sealed record LibraryDto(Guid Id, string Name, DateTimeOffset CreatedAt, LibraryRole Role);

/// <summary>
/// A member of a library. <c>Devices</c> (names of the paired phones that still work) and <c>LastSeenAt</c> (when a phone was last heard
/// from) are filled in for the owner only; everyone else gets <c>null</c> for both.
/// </summary>
public sealed record MemberDto(
    Guid UserId,
    string DisplayName,
    LibraryRole Role,
    DateTimeOffset JoinedAt,
    IReadOnlyList<string>? Devices = null,
    DateTimeOffset? LastSeenAt = null);
