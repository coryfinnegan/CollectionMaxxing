using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Authorization;

/// <summary>
/// Deny by default (docs/07, test 1): every endpoint must say how it is protected, and every protected endpoint
/// must be in the authorization matrix. These tests read the real routing table, so they also catch endpoints
/// added by code nobody remembered to list.
/// </summary>
public class EndpointPolicyTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static string Describe(RouteEndpoint endpoint) =>
        $"{string.Join("/", HttpMethods(endpoint))} {endpoint.RoutePattern.RawText}";

    private static IEnumerable<string> HttpMethods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];

    private List<RouteEndpoint> AllEndpoints() =>
        api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    private static bool AllowsAnonymous(RouteEndpoint e) => e.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    private static bool IsProtected(RouteEndpoint e) =>
        e.Metadata.GetMetadata<IAuthorizeData>() is not null
        || e.Metadata.GetMetadata<RequireLibraryRoleMetadata>() is not null;

    [Fact]
    public void Every_endpoint_requires_authorization_or_is_explicitly_anonymous()
    {
        var undeclared = AllEndpoints().Where(e => !AllowsAnonymous(e) && !IsProtected(e)).Select(Describe).ToList();

        undeclared.ShouldBeEmpty("These endpoints rely on the fallback policy only. Add RequireLibraryRole, RequireAuthorization or AllowAnonymous.");
    }

    [Fact]
    public void Only_allow_listed_endpoints_are_anonymous()
    {
        var anonymous = AllEndpoints()
            .Where(AllowsAnonymous)
            .Select(e => e.RoutePattern.RawText!)
            .Except(AuthorizationMatrix.AnonymousAllowList)
            .ToList();

        anonymous.ShouldBeEmpty("These endpoints allow anonymous callers but aren't on AuthorizationMatrix.AnonymousAllowList.");
    }

    [Fact]
    public void Every_endpoint_has_a_row_in_the_authorization_matrix_anonymous_ones_included()
    {
        var covered = AuthorizationMatrix.Cases.Select(c => (c.Method, c.Route)).ToHashSet();

        var missing = AllEndpoints()
            .Where(e => !AuthorizationMatrix.NotInMatrix.Contains(e.RoutePattern.RawText!))
            .SelectMany(e => HttpMethods(e).Select(method => (Method: method, Route: e.RoutePattern.RawText!)))
            .Where(endpoint => !covered.Contains(endpoint))
            .Select(endpoint => $"{endpoint.Method} {endpoint.Route}")
            .ToList();

        missing.ShouldBeEmpty("Add a row for each of these to AuthorizationMatrix.Cases.");
    }

    [Fact]
    public void The_matrix_lets_anonymous_callers_through_on_exactly_the_allow_listed_endpoints()
    {
        var anonymousRows = AuthorizationMatrix.Cases.Where(c => c.Anonymous != 401).Select(c => c.Route).Distinct().Order().ToList();
        var allowedRoutes = AuthorizationMatrix.AnonymousAllowList.Except(AuthorizationMatrix.NotInMatrix).Order().ToList();

        anonymousRows.ShouldBe(allowedRoutes);
    }

    [Fact]
    public void Every_row_in_the_authorization_matrix_is_a_real_endpoint()
    {
        var real = AllEndpoints()
            .SelectMany(e => HttpMethods(e).Select(method => (Method: method, Route: e.RoutePattern.RawText!)))
            .ToHashSet();

        var stale = AuthorizationMatrix.Cases.Where(c => !real.Contains((c.Method, c.Route))).Select(c => c.Name).ToList();

        stale.ShouldBeEmpty("These matrix rows don't match any endpoint (renamed or removed?).");
    }

    [Fact]
    public void Scan_session_endpoints_declare_the_minimum_role_they_need_in_the_sessions_library()
    {
        var sessionEndpoints = AllEndpoints()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/scan-sessions/", StringComparison.Ordinal))
            .ToList();

        sessionEndpoints.ShouldNotBeEmpty();
        sessionEndpoints
            .Where(e => e.Metadata.GetMetadata<RequireScanSessionRoleMetadata>() is null)
            .Select(Describe)
            .ShouldBeEmpty("Endpoints under /scan-sessions/{id} must call RequireScanSessionRole.");
    }

    [Fact]
    public void Admin_endpoints_require_the_admin_policy()
    {
        var adminEndpoints = AllEndpoints()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/admin/", StringComparison.Ordinal))
            .ToList();

        adminEndpoints.ShouldNotBeEmpty();
        adminEndpoints
            .Where(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == Policies.Admin))
            .Select(Describe)
            .ShouldBeEmpty("Endpoints under /api/admin must require the admin policy.");
    }

    [Fact]
    public void Library_endpoints_declare_the_minimum_role_they_need()
    {
        var libraryEndpoints = AllEndpoints()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/libraries/{libraryId", StringComparison.Ordinal))
            .ToList();

        libraryEndpoints.ShouldNotBeEmpty();
        libraryEndpoints
            .Where(e => e.Metadata.GetMetadata<RequireLibraryRoleMetadata>() is null)
            .Select(Describe)
            .ShouldBeEmpty("Endpoints under /libraries/{libraryId} must call RequireLibraryRole.");
    }
}
