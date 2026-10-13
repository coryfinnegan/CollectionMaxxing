using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Libraries;

// Response shapes of the libraries and members endpoints (docs/04). Fields are the contract: change them deliberately.

/// <summary>A library the caller belongs to, and their role in it.</summary>
public sealed record LibrarySummaryDto(Guid Id, string Name, LibraryRole Role);

public sealed record LibraryDto(Guid Id, string Name, DateTimeOffset CreatedAt, LibraryRole Role);

public sealed record MemberDto(Guid UserId, string DisplayName, LibraryRole Role, DateTimeOffset JoinedAt);
