using CollectionMaxxing.Api.Tests.Authorization;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// The hand-mutation proof of QA-1a, done on the REAL routing table: take what the app declares for every real endpoint, take away one
/// declaration (the library role, the scan-session role, the admin policy, the authorization itself) from one endpoint at a time, and the audit
/// must name that endpoint. This is the same fault as deleting the <c>RequireLibraryRole</c> call from the endpoint's source, without touching the
/// source. (<see cref="MatrixCompletenessTests"/> proves the audit's rules on made-up endpoints.)
/// </summary>
public class MatrixMutationTests(ApiFixture api)
{
    private IReadOnlyList<EndpointFacts> Real => EndpointFacts.Read(api.Factory.Services.GetRequiredService<EndpointDataSource>());

    private static List<string> AuditOf(IEnumerable<EndpointFacts> endpoints) =>
        EndpointAudit.Problems([.. endpoints], AuthorizationMatrix.Cases, AuthorizationMatrix.NotInMatrix);

    [Fact]
    public void The_real_table_is_clean_before_any_mutation()
    {
        AuditOf(Real).ShouldBeEmpty();
    }

    [Fact]
    public void Taking_the_library_role_off_any_single_library_endpoint_makes_the_audit_name_it()
    {
        var failures = new List<string>();
        foreach (var target in Real.Where(e => e.LibraryMinimum is not null))
        {
            var mutated = Real.Select(e => e == target ? e with { LibraryMinimum = null } : e);
            if (!AuditOf(mutated).Any(p => p.StartsWith(target.Name, StringComparison.Ordinal)))
            {
                failures.Add(target.Name);
            }
        }

        failures.ShouldBeEmpty();
        Real.Count(e => e.LibraryMinimum is not null).ShouldBeGreaterThan(30);
    }

    [Fact]
    public void Taking_the_scan_session_role_off_any_single_session_endpoint_makes_the_audit_name_it()
    {
        var failures = new List<string>();
        foreach (var target in Real.Where(e => e.ScanSessionMinimum is not null))
        {
            var mutated = Real.Select(e => e == target ? e with { ScanSessionMinimum = null } : e);
            if (!AuditOf(mutated).Any(p => p.StartsWith(target.Name, StringComparison.Ordinal)))
            {
                failures.Add(target.Name);
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Taking_the_admin_policy_off_any_single_admin_endpoint_makes_the_audit_name_it()
    {
        var failures = new List<string>();
        foreach (var target in Real.Where(e => e.RequiresAdminPolicy))
        {
            var mutated = Real.Select(e => e == target ? e with { RequiresAdminPolicy = false } : e);
            if (!AuditOf(mutated).Any(p => p.StartsWith(target.Name, StringComparison.Ordinal)))
            {
                failures.Add(target.Name);
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Making_any_single_protected_endpoint_anonymous_makes_the_audit_name_it()
    {
        var failures = new List<string>();
        foreach (var target in Real.Where(e => !e.AllowsAnonymous && !AuthorizationMatrix.NotInMatrix.Contains(e.Route)))
        {
            var mutated = Real.Select(e => e == target ? e with { AllowsAnonymous = true } : e);
            if (!AuditOf(mutated).Any(p => p.StartsWith(target.Name, StringComparison.Ordinal)))
            {
                failures.Add(target.Name);
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Dropping_the_authorization_of_any_single_signed_in_endpoint_makes_the_audit_name_it()
    {
        var failures = new List<string>();
        foreach (var target in Real.Where(e => !e.AllowsAnonymous && e.Minimum is null && !e.RequiresAdminPolicy && e.RequiresAuthorization))
        {
            var mutated = Real.Select(e => e == target ? e with { RequiresAuthorization = false } : e);
            if (!AuditOf(mutated).Any(p => p.StartsWith(target.Name, StringComparison.Ordinal)))
            {
                failures.Add(target.Name);
            }
        }

        failures.ShouldBeEmpty();
    }
}
