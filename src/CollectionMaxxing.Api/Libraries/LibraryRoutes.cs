using CollectionMaxxing.Api.Auth;

namespace CollectionMaxxing.Api.Libraries;

public static class LibraryRoutes
{
    public const string Prefix = "/api/v1/libraries/{libraryId:guid}";

    /// <summary>
    /// The route group every library-scoped endpoint hangs off. Its filter loads nothing itself (the caller's memberships
    /// are already known) and answers 404 or 403 before the endpoint runs. Each endpoint adds <c>RequireLibraryRole</c>.
    /// </summary>
    public static RouteGroupBuilder MapLibraryGroup(this IEndpointRouteBuilder app) =>
        app.MapGroup(Prefix).AddEndpointFilter<LibraryMembershipFilter>();
}
