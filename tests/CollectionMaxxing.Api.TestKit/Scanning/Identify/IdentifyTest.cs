using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

/// <summary>Base of the <c>POST …/identify</c> tests: helpers to post what the phone read, and to look at what was stored.</summary>
public abstract class IdentifyTest(ApiFixture api) : ScanTest(api)
{
    /// <summary>One item as the phone sends it. <paramref name="text"/> defaults to the title. Anything not given is sent as null.</summary>
    protected static Dictionary<string, object?> Read(
        string? title = null, string? text = null, string? kind = null, string? format = null, string? platform = null,
        string? completeness = null, int? year = null, string? creator = null, string? edition = null,
        Guid? localMatchReleaseId = null, string? localId = null, string? segmentId = null) => new()
        {
            ["localId"] = localId ?? Guid.NewGuid().ToString("N")[..8],
            ["segmentId"] = segmentId,
            ["text"] = text ?? title,
            ["title"] = title,
            ["kind"] = kind,
            ["format"] = format,
            ["platform"] = platform,
            ["completeness"] = completeness,
            ["year"] = year,
            ["creator"] = creator,
            ["edition"] = edition,
            ["localMatchReleaseId"] = localMatchReleaseId,
        };

    protected static Task<HttpResponseMessage> IdentifyAsync(HttpClient client, Guid sessionId, Guid? snapId, params object[] items) =>
        client.PostAsync(IdentifyUrl(sessionId), Json(Body(snapId ?? Guid.NewGuid(), items)));

    protected static Dictionary<string, object?> Body(Guid snapId, IEnumerable<object> items) => new()
    {
        ["snapId"] = snapId,
        ["trigger"] = "auto",
        ["engine"] = "apple_model",
        ["items"] = items.ToArray(),
    };

    protected static string IdentifyUrl(Guid sessionId) => $"{SessionUrl(sessionId)}/identify";

    /// <summary>Posts the items and returns the candidates of the 200 response, in order.</summary>
    protected static async Task<List<JsonElement>> IdentifyOkAsync(HttpClient client, Guid sessionId, params object[] items)
    {
        var response = await IdentifyAsync(client, sessionId, null, items);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    protected static List<string> AlternativeTitles(JsonElement candidate) =>
        candidate.GetProperty("alternatives").ValueKind == JsonValueKind.Array
            ? candidate.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("title").GetString()!).ToList()
            : [];

    protected async Task RunNextJobAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<JobRunner>().RunNextAsync("test-worker", CancellationToken.None);
    }

    protected async Task<int> CountCandidatesAsync(Guid sessionId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().CountAsync(c => c.SessionId == sessionId);
    }

    protected async Task<int> CountSnapsAsync(Guid sessionId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanSnaps.IgnoreQueryFilters().CountAsync(s => s.SessionId == sessionId);
    }
}
