using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Libraries;

/// <summary>Body of <c>PATCH …/libraries/{lib}</c> (JSON Merge Patch).</summary>
public sealed record UpdateLibraryRequest(string? Name = null);

/// <summary>Body of <c>PATCH …/members/{userId}</c>. The owner's role can't be changed this way; ownership is transferred separately.</summary>
public sealed record UpdateMemberRequest(LibraryRole? Role = null);

/// <summary>Body of <c>POST …/invites</c>: who it is for and the role they get (editor or viewer).</summary>
public sealed record CreateInviteRequest(string? Name = null, LibraryRole? Role = null);
