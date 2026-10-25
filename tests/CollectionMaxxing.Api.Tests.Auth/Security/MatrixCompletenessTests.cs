using CollectionMaxxing.Api.Tests.Authorization;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Security;

// LOCKED acceptance tests of QA-1a (issue #166), the endpoint audit. Do not edit, weaken, skip or delete: add new tests instead.
public class MatrixCompletenessTests(ApiFixture api)
{
    private IReadOnlyList<EndpointFacts> Real => EndpointFacts.Read(api.Factory.Services.GetRequiredService<EndpointDataSource>());

    [Fact]
    public void Every_endpoint_has_exactly_one_matrix_row_and_every_row_is_a_real_endpoint()
    {
        var problems = EndpointAudit.Problems(Real, AuthorizationMatrix.Cases, AuthorizationMatrix.NotInMatrix)
            .Where(p => p.Contains("no row in", StringComparison.Ordinal) || p.Contains("no such endpoint", StringComparison.Ordinal) || p.Contains("matrix rows", StringComparison.Ordinal))
            .ToList();

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Every_row_agrees_with_what_its_endpoint_declares_and_every_family_of_routes_carries_its_declaration()
    {
        EndpointAudit.Problems(Real, AuthorizationMatrix.Cases, AuthorizationMatrix.NotInMatrix).ShouldBeEmpty();
    }

    [Fact]
    public void The_real_routing_table_has_the_endpoints_the_audit_expects_to_find()
    {
        // A guard against the audit passing because it saw nothing.
        Real.Count.ShouldBeGreaterThan(75);
        Real.Count(e => e.LibraryMinimum is not null).ShouldBeGreaterThan(30);
        Real.Count(e => e.ScanSessionMinimum is not null).ShouldBeGreaterThan(8);
        Real.Count(e => e.RequiresAdminPolicy).ShouldBeGreaterThan(3);
        Real.Count(e => e.AllowsAnonymous).ShouldBeGreaterThan(7); // ONB-1 (#249) removed GET /setup, POST /setup and GET /account/not-linked: 8 remain
    }

    // ---- the audit must bite: each of these made-up worlds has one fault and the audit must name it ---------------------

    private static readonly EndpointCase Row = new("GET", "/api/v1/libraries/{libraryId:guid}/things", null, 401, 404, 200, 200, 200);

    private static EndpointFacts Facts(string route = "/api/v1/libraries/{libraryId:guid}/things", LibraryRole? minimum = LibraryRole.Viewer, bool anonymous = false, bool admin = false, string method = "GET") =>
        new(method, route, anonymous, true, admin, minimum, null);

    private static List<string> Audit(EndpointFacts[] endpoints, params EndpointCase[] rows) =>
        EndpointAudit.Problems(endpoints, rows, []);

    [Fact]
    public void An_endpoint_with_no_matrix_row_is_reported()
    {
        Audit([Facts(), Facts("/api/v1/libraries/{libraryId:guid}/forgotten")], Row)
            .ShouldContain(p => p.Contains("forgotten", StringComparison.Ordinal) && p.Contains("no row", StringComparison.Ordinal));
    }

    [Fact]
    public void A_matrix_row_with_no_endpoint_is_reported()
    {
        Audit([Facts()], Row, Row with { Route = "/api/v1/libraries/{libraryId:guid}/renamed" })
            .ShouldContain(p => p.Contains("renamed", StringComparison.Ordinal) && p.Contains("no such endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_rows_for_one_endpoint_are_reported()
    {
        Audit([Facts()], Row, Row).ShouldContain(p => p.Contains("2 matrix rows", StringComparison.Ordinal));
    }

    [Fact]
    public void A_library_endpoint_with_no_role_declaration_is_reported()
    {
        Audit([Facts(minimum: null)], Row).ShouldContain(p => p.Contains("no RequireLibraryRole", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scan_session_endpoint_with_no_role_declaration_is_reported()
    {
        var facts = new EndpointFacts("GET", "/api/v1/scan-sessions/{id:guid}", false, true, false, null, null);
        var row = new EndpointCase("GET", "/api/v1/scan-sessions/{id:guid}", null, 401, 404, 200, 200, 200);

        Audit([facts], row).ShouldContain(p => p.Contains("no RequireScanSessionRole", StringComparison.Ordinal));
    }

    [Fact]
    public void An_admin_route_outside_the_admin_policy_is_reported()
    {
        var facts = new EndpointFacts("GET", "/api/admin/things", false, true, false, null, null);
        var row = new EndpointCase("GET", "/api/admin/things", null, 401, 403, 403, 403, 403) { Admin = 200 };

        Audit([facts], row).ShouldContain(p => p.Contains("not behind the admin policy", StringComparison.Ordinal));
    }

    [Fact]
    public void An_endpoint_with_no_declaration_at_all_is_reported()
    {
        var facts = new EndpointFacts("GET", "/api/v1/stray", false, false, false, null, null);
        var row = new EndpointCase("GET", "/api/v1/stray", null, 401, 200, 200, 200, 200);

        Audit([facts], row).ShouldContain(p => p.Contains("declares no authorization", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_that_lets_anonymous_callers_into_an_endpoint_that_requires_sign_in_is_reported()
    {
        Audit([Facts()], Row with { Anonymous = 200 }).ShouldContain(p => p.Contains("anonymous callers get 200", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_that_hides_an_anonymous_endpoint_is_reported()
    {
        Audit([Facts(anonymous: true)], Row).ShouldContain(p => p.Contains("allows anonymous callers but the matrix says 401", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_that_lets_a_viewer_through_an_editor_endpoint_is_reported()
    {
        var editorEndpoint = Facts(minimum: LibraryRole.Editor, method: "POST");
        var loosened = Row with { Method = "POST", Viewer = 201, Editor = 201, Owner = 201 };

        Audit([editorEndpoint], loosened).ShouldContain(p => p.Contains("needs Editor", StringComparison.Ordinal) && p.Contains("Viewer", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_that_tells_a_stranger_403_instead_of_404_is_reported()
    {
        Audit([Facts()], Row with { OtherUser = 403 }).ShouldContain(p => p.Contains("non-member must get 404", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_that_refuses_a_role_that_may_call_is_reported()
    {
        Audit([Facts()], Row with { Editor = 403 }).ShouldContain(p => p.Contains("Editor may call it", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_that_lets_a_library_owner_into_an_admin_route_is_reported()
    {
        var facts = new EndpointFacts("GET", "/api/admin/things", false, true, true, null, null);
        var row = new EndpointCase("GET", "/api/admin/things", null, 401, 403, 403, 403, 200) { Admin = 200 };

        Audit([facts], row).ShouldContain(p => p.Contains("admin only", StringComparison.Ordinal) && p.Contains("Owner", StringComparison.Ordinal));
    }

    [Fact]
    public void A_world_with_no_fault_has_no_problems()
    {
        Audit([Facts()], Row).ShouldBeEmpty();
    }
}
