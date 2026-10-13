using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Http;

namespace CollectionMaxxing.Api.Auth;

public static class RequireLibraryRoleExtensions
{
    /// <summary>
    /// Declares the lowest library role that may call the endpoint. The <see cref="LibraryMembershipFilter"/> on the
    /// library route group enforces it: not a member is 404, a member with too low a role is 403.
    /// </summary>
    public static TBuilder RequireLibraryRole<TBuilder>(this TBuilder builder, LibraryRole minimumRole)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization(); // anonymous callers are turned away (401) before any library is looked up
        builder.WithMetadata(new RequireLibraryRoleMetadata(minimumRole));
        builder.ProducesProblem(StatusCodes.Status401Unauthorized);
        builder.ProducesProblem(StatusCodes.Status403Forbidden);
        builder.ProducesProblem(StatusCodes.Status404NotFound);
        return builder;
    }
}
