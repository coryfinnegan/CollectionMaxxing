using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning;

/// <summary>Fixes found when SCAN-1a was verified: the user's word wins over the job, someone else's session is always 404, and a retry replays.</summary>
public class ScanReviewFixTests(ApiFixture api) : ScanTest(api)
{
    [Fact]
    public async Task A_rejection_made_while_a_code_is_pending_survives_the_resolve_job()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9301);
        var slow = true;
        Products.Behaviour = async (id, ct) =>
        {
            if (slow)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }

            return Application.Scanning.ProductLookupResult.Found(ScanData.Product("Mario Golf", code));
        };
        Games.Results.Add(ScanData.Game("Mario Golf"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, code);
        Resolution(candidates[0]).ShouldBe("pending");

        (await DecideAsync(editor, sessionId, Id(candidates[0]), new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        slow = false;
        using (var scope = Factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<JobRunner>().RunNextAsync("test-worker", CancellationToken.None)).ShouldBeTrue();
        }

        var resolved = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(resolved).ShouldBe("matched");
        Decision(resolved).ShouldBe("rejected");
    }

    [Theory]
    [InlineData("PATCH", "/candidates/{cid}")]
    [InlineData("POST", "/candidates/{cid}:search")]
    [InlineData("POST", "/candidates/{cid}/photo")]
    [InlineData("POST", "/commit")]
    [InlineData("DELETE", "")]
    public async Task A_viewer_gets_404_not_403_on_an_editors_session_for_every_editor_only_endpoint(string method, string path)
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9302);
        await CatalogAsync("Wave Race 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await ScanAsync(editor, sessionId, code))[0]);

        using var viewer = As(library, LibraryRole.Viewer);
        using var request = new HttpRequestMessage(new HttpMethod(method), SessionUrl(sessionId) + path.Replace("{cid}", candidateId.ToString(), StringComparison.Ordinal));
        if (method != "DELETE" && !path.EndsWith("/photo", StringComparison.Ordinal))
        {
            request.Content = Json(new { });
        }

        if (path == "/commit")
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        using var response = await viewer.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path}");
    }

    [Fact]
    public async Task A_former_member_gets_404_on_their_own_old_session()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9303);
        await CatalogAsync("Pilotwings 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await ScanAsync(editor, sessionId, code))[0]);

        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.UserId == library.EditorId).ExecuteDeleteAsync();
        }

        (await editor.GetAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DecideAsync(editor, sessionId, candidateId, new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await editor.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retrying_barcodes_with_the_same_key_after_commit_replays_the_original_answer()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9304);
        await CatalogAsync("Excitebike 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var first = await SubmitAsync(editor, sessionId, "retry-after-commit", Code(code));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstIds = IdsOf(await ReadJsonAsync(first));
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var retry = await SubmitAsync(editor, sessionId, "retry-after-commit", Code(code));

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        IdsOf(await ReadJsonAsync(retry)).ShouldBe(firstIds);
    }

    [Fact]
    public async Task Retrying_barcodes_with_the_same_key_after_abandon_replays_the_original_answer()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9305);
        await CatalogAsync("Diddy Kong Racing", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var first = await SubmitAsync(editor, sessionId, "retry-after-abandon", Code(code));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstIds = IdsOf(await ReadJsonAsync(first));
        (await editor.DeleteAsync(SessionUrl(sessionId))).IsSuccessStatusCode.ShouldBeTrue();

        var retry = await SubmitAsync(editor, sessionId, "retry-after-abandon", Code(code));

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        IdsOf(await ReadJsonAsync(retry)).ShouldBe(firstIds);
    }

    private static List<Guid> IdsOf(System.Text.Json.JsonElement body) =>
        body.GetProperty("candidates").EnumerateArray().Select(Id).ToList();
}
