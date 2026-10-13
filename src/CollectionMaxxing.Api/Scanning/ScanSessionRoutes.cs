using CollectionMaxxing.Api.Auth;

namespace CollectionMaxxing.Api.Scanning;

public static class ScanSessionRoutes
{
    public const string Prefix = "/api/v1/scan-sessions";

    /// <summary>
    /// The route group every endpoint under /scan-sessions/{id} hangs off (each maps its own "/{id:guid}..." route). Its filter looks up the session's library and answers 404 or
    /// 403 before the endpoint runs, and throws for an endpoint that forgot <c>RequireScanSessionRole</c> (fail closed).
    /// </summary>
    public static RouteGroupBuilder MapScanSessionGroup(this IEndpointRouteBuilder app) =>
        app.MapGroup(Prefix).AddEndpointFilter<ScanSessionAccessFilter>();
}
