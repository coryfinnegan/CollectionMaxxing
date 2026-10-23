using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Libraries;

/// <summary>Body of <c>POST /api/v1/libraries</c>: the new library's name, 1 to 80 characters once trimmed.</summary>
public sealed record CreateLibraryRequest(string? Name = null);

/// <summary>Body of <c>POST /api/v1/invites:preview</c> and <c>POST /api/v1/invites:accept</c>: an invite link, a link's token, or a code.</summary>
public sealed record InviteLookupRequest(string? Invite = null);

/// <summary>Body of <c>PATCH …/libraries/{lib}</c> (JSON Merge Patch).</summary>
public sealed record UpdateLibraryRequest(string? Name = null);

/// <summary>Body of <c>PATCH …/members/{userId}</c>. The owner's role can't be changed this way; ownership is transferred separately.</summary>
public sealed record UpdateMemberRequest(LibraryRole? Role = null);

/// <summary>Body of <c>POST …/invites</c>: who it is for and the role they get (editor or viewer).</summary>
public sealed record CreateInviteRequest(string? Name = null, LibraryRole? Role = null);
