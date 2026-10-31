using System.Text.Json;
using System.Text.Json.Nodes;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Contract;

/// <summary>
/// The API contract (docs/04, "Contract workflow"). <c>contracts/openapi.json</c> is committed; the app's real
/// OpenAPI document must equal it. The TypeScript client is generated from the committed file.
/// </summary>
public class OpenApiContractTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    public const string UpdateCommand = "dotnet build src/CollectionMaxxing.Api -p:UpdateContract=true";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private async Task<JsonNode> LiveDocumentAsync()
    {
        using var client = api.CreateClient();
        var json = await client.GetStringAsync("/openapi/v1.json");
        return JsonNode.Parse(json)!;
    }

    [Fact]
    public async Task The_committed_contract_matches_the_document_the_app_generates()
    {
        var live = await LiveDocumentAsync();
        var path = RepositoryFiles.Find("contracts", "openapi.json");

        File.Exists(path).ShouldBeTrue($"contracts/openapi.json doesn't exist yet. Create it with:\n    {UpdateCommand}");
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(path))!;

        if (!JsonNode.DeepEquals(live, committed))
        {
            Assert.Fail(
                "The API contract changed. If that is intended, review the difference and update the snapshot with:\n"
                + $"    {UpdateCommand}\n\nFirst differences (committed → generated):\n{Differences(committed, live)}");
        }
    }

    [Fact]
    public async Task Every_operation_has_a_unique_operation_id_for_the_client_generator()
    {
        var live = await LiveDocumentAsync();

        var operations = live["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(operation => (Path: path.Key, Method: operation.Key, Id: operation.Value!["operationId"]?.GetValue<string>())))
            .ToList();

        operations.Where(o => string.IsNullOrEmpty(o.Id)).Select(o => $"{o.Method} {o.Path}").ShouldBeEmpty("Add .WithName(\"…\") to these endpoints.");
        operations.Select(o => o.Id).Distinct().Count().ShouldBe(operations.Count, "operationIds must be unique");
    }

    [Fact]
    public async Task The_document_does_not_depend_on_the_host_that_served_it()
    {
        var live = await LiveDocumentAsync();

        (live["servers"] is null || live["servers"]!.AsArray().Count == 0).ShouldBeTrue();
    }

    private static string Differences(JsonNode committed, JsonNode live)
    {
        var left = committed.ToJsonString(Indented).Split('\n');
        var right = live.ToJsonString(Indented).Split('\n');
        var lines = new List<string>();
        for (var i = 0; i < Math.Max(left.Length, right.Length) && lines.Count < 10; i++)
        {
            var a = i < left.Length ? left[i].TrimEnd('\r') : "<end>";
            var b = i < right.Length ? right[i].TrimEnd('\r') : "<end>";
            if (a != b)
            {
                lines.Add($"  line {i + 1}: {a.Trim()}  →  {b.Trim()}");
            }
        }

        return string.Join('\n', lines);
    }
}
