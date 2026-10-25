using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning.Identify;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// Base of the eval-server tests: the real app with the evals switched on and pointed at a generated golden folder and a results
/// folder in the temp directory (never at the owner's photos), with the same fake providers the scanning tests use.
/// </summary>
public abstract class EvalTest : IdentifyTest
{
    protected const string Base = "/api/admin/evals";

    protected EvalTest(ApiFixture api)
        : base(api)
    {
        Settings["Evals:Enabled"] = "true";
        Settings["Evals:GoldenPath"] = Golden.Golden;
        Settings["Evals:ResultsPath"] = Golden.Results;
    }

    /// <summary>The generated golden set and results folders of this test.</summary>
    protected SyntheticGolden Golden { get; } = new();

    public override async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await base.DisposeAsync();
        Golden.Dispose();
    }

    /// <summary>A signed-in platform administrator (the owner of the PC), who belongs to no library.</summary>
    protected HttpClient AdminClient()
    {
        var client = AsUser(Guid.CreateVersion7());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "admin");
        return client;
    }

    protected static object ReadItem(
        string title, string? kind = "game", string? format = "game_cartridge", string? platform = null, string? completeness = null,
        int? year = null, string? resolvedBy = null) => new
        {
            title,
            text = title,
            kind,
            format,
            platform,
            completeness,
            year,
            resolvedBy,
        };

    /// <summary>One photo's result as the phone uploads it.</summary>
    protected static object PhotoResult(
        string photoId, string engine, object[] items, string outcome = "ok", long latencyMs = 1500, long? firstItemMs = 800,
        string thermalState = "nominal", int unreadableCount = 0, string? fellBackFrom = null) => new
        {
            photoId,
            engine,
            outcome,
            fellBackFrom,
            items,
            unreadableCount,
            latencyMs,
            firstItemMs,
            thermalState,
        };

    /// <summary>A whole run body.</summary>
    protected static Dictionary<string, object?> RunBody(params object[] results) => new()
    {
        ["device"] = new { model = "iPhone18,1", osVersion = "26.0", appVersion = "0.3.0" },
        ["configVersion"] = "cfg-test-1",
        ["results"] = results,
    };

    protected static Task<HttpResponseMessage> PostRunAsync(HttpClient client, object body) => client.PostAsync($"{Base}/runs", Json(body));

    protected static async Task<JsonElement> PostRunOkAsync(HttpClient client, object body)
    {
        var response = await PostRunAsync(client, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadJsonAsync(response);
    }

    /// <summary>The dated result files (<c>yyyy-MM-dd-&lt;sha&gt;.json</c>) in the results folder.</summary>
    protected IReadOnlyList<string> ResultFiles() =>
        Directory.Exists(Golden.Results)
            ? Directory.GetFiles(Golden.Results, "????-??-??-*.json").Order().ToList()
            : [];
}
