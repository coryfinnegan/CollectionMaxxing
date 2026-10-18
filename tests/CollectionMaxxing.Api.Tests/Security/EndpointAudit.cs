using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Authorization;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>What the routing table says about one endpoint and one HTTP method (the audit reads this, never the source code).</summary>
public sealed record EndpointFacts(
    string Method,
    string Route,
    bool AllowsAnonymous,
    bool RequiresAuthorization,
    bool RequiresAdminPolicy,
    LibraryRole? LibraryMinimum,
    LibraryRole? ScanSessionMinimum)
{
    public string Name => $"{Method} {Route}";

    /// <summary>The lowest library role the endpoint declares, in the route's library or the scan session's library.</summary>
    public LibraryRole? Minimum => LibraryMinimum ?? ScanSessionMinimum;

    public static IReadOnlyList<EndpointFacts> Read(EndpointDataSource source) =>
    [
        .. source.Endpoints.OfType<RouteEndpoint>().SelectMany(endpoint =>
            (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(method =>
                new EndpointFacts(
                    method,
                    endpoint.RoutePattern.RawText!,
                    endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                    endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null,
                    endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == Policies.Admin),
                    endpoint.Metadata.GetMetadata<RequireLibraryRoleMetadata>()?.MinimumRole,
                    endpoint.Metadata.GetMetadata<RequireScanSessionRoleMetadata>()?.MinimumRole))),
    ];
}

/// <summary>
/// The endpoint audit as a function, so the same checks run against the real routing table and against made-up ones that must
/// fail (<see cref="MatrixCompletenessTests"/>). It compares what each endpoint DECLARES (anonymous, admin policy, minimum role)
/// with what <see cref="AuthorizationMatrix"/> EXPECTS of each kind of caller, so a row cannot be loosened without a test noticing.
/// </summary>
public static class EndpointAudit
{
    public const string LibraryPrefix = "/api/v1/libraries/{libraryId";
    public const string SessionPrefix = "/api/v1/scan-sessions/";
    public const string AdminPrefix = "/api/admin/";

    public static List<string> Problems(
        IReadOnlyCollection<EndpointFacts> endpoints,
        IReadOnlyCollection<EndpointCase> rows,
        IReadOnlyCollection<string> notInMatrix)
    {
        var problems = new List<string>();
        var inMatrix = endpoints.Where(e => !notInMatrix.Contains(e.Route)).ToList();

        // 1. Both ways: no endpoint without a row, no row without an endpoint, no endpoint with two rows.
        foreach (var endpoint in inMatrix.Where(e => !rows.Any(r => r.Method == e.Method && r.Route == e.Route)))
        {
            problems.Add($"{endpoint.Name}: no row in the authorization matrix");
        }

        foreach (var row in rows.Where(r => !endpoints.Any(e => e.Method == r.Method && e.Route == r.Route)))
        {
            problems.Add($"{row.Name}: a matrix row but no such endpoint");
        }

        foreach (var duplicate in rows.GroupBy(r => (r.Method, r.Route)).Where(g => g.Count() > 1))
        {
            problems.Add($"{duplicate.Key.Method} {duplicate.Key.Route}: {duplicate.Count()} matrix rows");
        }

        // 2. Each endpoint says how it is protected, and the row agrees with what it says.
        foreach (var endpoint in inMatrix)
        {
            var row = rows.FirstOrDefault(r => r.Method == endpoint.Method && r.Route == endpoint.Route);
            if (!endpoint.AllowsAnonymous && !endpoint.RequiresAuthorization && endpoint.Minimum is null)
            {
                problems.Add($"{endpoint.Name}: declares no authorization at all (only the fallback policy stands guard)");
            }

            if (row is null)
            {
                continue;
            }

            problems.AddRange(Disagreements(endpoint, row));
        }

        // 3. Whole families of routes must carry the declaration that goes with their prefix.
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Route.StartsWith(LibraryPrefix, StringComparison.Ordinal) && endpoint.LibraryMinimum is null)
            {
                problems.Add($"{endpoint.Name}: under a library route but no RequireLibraryRole");
            }

            if (endpoint.Route.StartsWith(SessionPrefix, StringComparison.Ordinal) && endpoint.ScanSessionMinimum is null)
            {
                problems.Add($"{endpoint.Name}: under a scan session but no RequireScanSessionRole");
            }

            if (endpoint.Route.StartsWith(AdminPrefix, StringComparison.Ordinal) && !endpoint.RequiresAdminPolicy)
            {
                problems.Add($"{endpoint.Name}: under /api/admin but not behind the admin policy");
            }
        }

        return problems;
    }

    private static IEnumerable<string> Disagreements(EndpointFacts endpoint, EndpointCase row)
    {
        if (endpoint.AllowsAnonymous && row.Anonymous == 401)
        {
            yield return $"{endpoint.Name}: allows anonymous callers but the matrix says 401";
        }

        if (!endpoint.AllowsAnonymous && row.Anonymous != 401)
        {
            yield return $"{endpoint.Name}: requires sign-in but the matrix lets anonymous callers get {row.Anonymous}";
        }

        if (endpoint.RequiresAdminPolicy)
        {
            foreach (var actor in new[] { Actor.OtherUser, Actor.Viewer, Actor.Editor, Actor.Owner })
            {
                if (row.ExpectedFor(actor) != 403)
                {
                    yield return $"{endpoint.Name}: admin only, but the matrix gives {actor} {row.ExpectedFor(actor)} instead of 403";
                }
            }

            if (row.ExpectedFor(Actor.Admin) is 401 or 403)
            {
                yield return $"{endpoint.Name}: admin only, but the matrix refuses the admin ({row.ExpectedFor(Actor.Admin)})";
            }

            yield break;
        }

        if (endpoint.Minimum is { } minimum)
        {
            // Not a member of the library: 404, never 403 (don't reveal that it exists).
            if (row.OtherUser != 404)
            {
                yield return $"{endpoint.Name}: a non-member must get 404 but the matrix says {row.OtherUser}";
            }

            foreach (var (actor, role) in new[] { (Actor.Viewer, LibraryRole.Viewer), (Actor.Editor, LibraryRole.Editor), (Actor.Owner, LibraryRole.Owner) })
            {
                var expected = row.ExpectedFor(actor);
                if (role < minimum && expected != 403)
                {
                    yield return $"{endpoint.Name}: needs {minimum} but the matrix gives a {role} {expected} instead of 403";
                }

                if (role >= minimum && expected is 401 or 403)
                {
                    yield return $"{endpoint.Name}: a {role} may call it (needs {minimum}) but the matrix says {expected}";
                }
            }
        }
    }
}
