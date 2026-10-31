using System.Text.RegularExpressions;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// <c>docs/security/endpoint-audit.md</c> is the written audit. It stays true because this test fails when an endpoint is missing from its
/// table, when the table lists an endpoint that does not exist, and when a row disagrees with the code about who may call it.
/// </summary>
public partial class EndpointAuditDocTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private sealed record Row(string Method, string Route, string Policy, string Who);

    private static List<Row> ReadTable()
    {
        var text = File.ReadAllText(RepositoryFiles.Find("docs", "security", "endpoint-audit.md"));
        return [.. text.Split('\n').Select(line => TableRow().Match(line)).Where(m => m.Success)
            .Select(m => new Row(m.Groups["method"].Value, m.Groups["route"].Value, m.Groups["policy"].Value.Trim(), m.Groups["who"].Value.Trim()))];
    }

    [GeneratedRegex(@"^\| (?<method>[A-Z]+) \| `(?<route>[^`]+)` \| (?<policy>[^|]+) \| (?<who>[^|]+) \|")]
    private static partial Regex TableRow();

    private IReadOnlyList<EndpointFacts> Real => EndpointFacts.Read(api.Factory.Services.GetRequiredService<EndpointDataSource>());

    private static string Normal(string method) => method == "*" ? "GET" : method;

    [Fact]
    public void Every_endpoint_is_a_row_of_the_audit_and_every_row_is_an_endpoint()
    {
        var table = ReadTable().Select(r => (r.Method, r.Route)).ToHashSet();
        var real = Real.Select(e => (Method: Normal(e.Method), e.Route)).ToHashSet();

        real.Except(table).Select(e => $"{e.Method} {e.Route}").Order().ShouldBeEmpty("Add these endpoints to docs/security/endpoint-audit.md.");
        table.Except(real).Select(e => $"{e.Method} {e.Route}").Order().ShouldBeEmpty("These rows of docs/security/endpoint-audit.md are not endpoints.");
        ReadTable().Count.ShouldBe(table.Count, "A row is listed twice.");
    }

    [Fact]
    public void Each_rows_policy_and_roles_are_what_the_endpoint_declares()
    {
        var table = ReadTable().ToDictionary(r => (r.Method, r.Route));
        var wrong = new List<string>();
        foreach (var endpoint in Real)
        {
            var row = table[(Normal(endpoint.Method), endpoint.Route)];
            var expected = endpoint switch
            {
                { RequiresAdminPolicy: true } => ("admin policy", "platform admin only"),
                { LibraryMinimum: { } minimum } => ("library role", $"{minimum.ToString().ToLowerInvariant()} and above"),
                { ScanSessionMinimum: { } minimum } => ("scan-session role", $"{minimum.ToString().ToLowerInvariant()} and above (in the session's library)"),
                { AllowsAnonymous: true } when EndpointAudit.IsPlumbing(endpoint.Route) => ("anonymous (platform plumbing)", "everyone"),
                { AllowsAnonymous: true } => ("anonymous (allow-listed)", "everyone"),
                _ => ("signed in", "any signed-in caller (own data only)"),
            };
            if ((row.Policy, row.Who) != expected)
            {
                wrong.Add($"{row.Method} {row.Route}: the audit says '{row.Policy} / {row.Who}', the code declares '{expected.Item1} / {expected.Item2}'");
            }
        }

        wrong.ShouldBeEmpty();
    }
}
