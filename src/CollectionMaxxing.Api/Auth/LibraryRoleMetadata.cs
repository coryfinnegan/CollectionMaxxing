using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Auth;

/// <summary>Endpoint metadata: the lowest library role allowed to call this endpoint. Added by <c>RequireLibraryRole</c>.</summary>
public sealed record RequireLibraryRoleMetadata(LibraryRole MinimumRole);
