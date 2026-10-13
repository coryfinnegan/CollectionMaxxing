using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Http;

namespace CollectionMaxxing.Api.Auth;

public static class RequireLibraryRoleExtensions
{
    /// <summary>
    /// Declares the lowest library role that may call the endpoint, and enforces it: this attaches the
    /// <see cref="LibraryMembershipFilter"/> to the endpoint itself, so it is guarded wherever it is mapped (not a
    /// member is 404, a member with too low a role is 403). The library route group also carries the filter so an
    /// endpoint that forgets this call still fails closed; the filter runs once per request, so an endpoint that has
    /// both is checked once.
    /// </summary>
    public static TBuilder RequireLibraryRole<TBuilder>(this TBuilder builder, LibraryRole minimumRole)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization(); // anonymous callers are turned away (401) before any library is looked up
        builder.WithMetadata(new RequireLibraryRoleMetadata(minimumRole));
        builder.AddEndpointFilter<TBuilder, LibraryMembershipFilter>();
        builder.ProducesProblem(StatusCodes.Status401Unauthorized);
        builder.ProducesProblem(StatusCodes.Status403Forbidden);
        builder.ProducesProblem(StatusCodes.Status404NotFound);
        return builder;
    }
}
