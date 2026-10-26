using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning;

// Issue #339 (design §8(c)): POST …/barcodes accepts an optional candidateId binding. A stable read from
// Review's "Scan barcode" upgrades that row in place through the barcode (T0) path: same candidate, new answer.
public class BoundBarcodeTests(ApiFixture api) : ScanTest(api)
{
    private static Task<HttpResponseMessage> SubmitBoundAsync(HttpClient client, Guid sessionId, Guid candidateId, params object[] codes)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/barcodes")
        {
            Content = Json(new { codes, candidateId }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> FirstCandidateAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates")[0];
    }

    private async Task<bool> WasLocalHitAsync(Guid candidateId)
    {
        await using var context = Db.CreateContext();
        var trace = await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == candidateId).Select(c => c.TraceJson).SingleAsync();
        return trace!.Contains("\"catalogue\"", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABoundBarcodeUpgradesItsReviewRowInPlace()
    {
        var library = await CreateLibraryAsync();
        var reused = ScanData.Upc(6301);
        await CatalogAsync("Reused E", reused);
        await CatalogAsync("Reused F", reused);
        var code = ScanData.Upc(6302);
        Products.Know(code, ScanData.Product("Wave Race 64", code));
        Games.Results.Add(ScanData.Game("Wave Race 64"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await ScanAsync(editor, sessionId, reused))[0];
        Resolution(row).ShouldBe("ambiguous");

        var upgraded = await FirstCandidateAsync(await SubmitBoundAsync(editor, sessionId, Id(row), Code(code)));

        Id(upgraded).ShouldBe(Id(row), "the row is upgraded in place, not replaced");
        Resolution(upgraded).ShouldBe("matched");
        upgraded.GetProperty("release").GetProperty("title").GetString().ShouldBe("Wave Race 64");
        upgraded.GetProperty("rawCode").GetString().ShouldBe(ScanData.Gtin13(code));
        Decision(upgraded).ShouldBe("accepted", "a barcode-proof match is accepted, the T0 path");
        (await WasLocalHitAsync(Id(row))).ShouldBeFalse("the answer came from the providers");
    }

    [Fact]
    public async Task ABoundBarcodeUpgradesAQuotaPendingRowInPlace()
    {
        var library = await CreateLibraryAsync();
        var waiting = ScanData.Upc(6311);
        var code = ScanData.Upc(6312);
        var product = ScanData.Product("1080 Snowboarding", code);
        Products.Behaviour = (id, _) => Task.FromResult(
            id.Value == ScanData.Gtin13(code) ? ProductLookupResult.Found(product) : ProductLookupResult.QuotaExhausted);
        Games.Results.Add(ScanData.Game("1080 Snowboarding"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await ScanAsync(editor, sessionId, waiting))[0];
        Resolution(row).ShouldBe("pending");
        row.GetProperty("pendingReason").GetString().ShouldBe("quota");

        var upgraded = await FirstCandidateAsync(await SubmitBoundAsync(editor, sessionId, Id(row), Code(code)));

        Id(upgraded).ShouldBe(Id(row));
        Resolution(upgraded).ShouldBe("matched");
        upgraded.GetProperty("release").GetProperty("title").GetString().ShouldBe("1080 Snowboarding");
        PendingReason(upgraded).ShouldBeNull("the quota wait is gone with the upgrade");
    }

    private static string? PendingReason(System.Text.Json.JsonElement candidate) =>
        candidate.TryGetProperty("pendingReason", out var reason) && reason.ValueKind == System.Text.Json.JsonValueKind.String ? reason.GetString() : null;

    [Fact]
    public async Task ABoundScanOfALearnedCodeMakesNoProviderCall()
    {
        var library = await CreateLibraryAsync();
        var reused = ScanData.Upc(6321);
        await CatalogAsync("Reused G", reused);
        await CatalogAsync("Reused H", reused);
        var known = ScanData.Upc(6322);
        await CatalogAsync("Excitebike 64", known);
        var searches = 0;
        Games.Behaviour = _ =>
        {
            searches++;
            return Task.CompletedTask;
        };
        Books.Behaviour = _ =>
        {
            searches++;
            return Task.CompletedTask;
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await ScanAsync(editor, sessionId, reused))[0];
        Resolution(row).ShouldBe("ambiguous");

        var upgraded = await FirstCandidateAsync(await SubmitBoundAsync(editor, sessionId, Id(row), Code(known)));

        Resolution(upgraded).ShouldBe("matched");
        Products.Calls.ShouldBe(0, "the learned layer answers first");
        searches.ShouldBe(0, "the learned layer answers first");
        (await WasLocalHitAsync(Id(row))).ShouldBeTrue();
    }

    [Fact]
    public async Task ACommittedBoundRowTeachesTheLearnedLayer()
    {
        var library = await CreateLibraryAsync();
        var reused = ScanData.Upc(6331);
        await CatalogAsync("Reused I", reused);
        await CatalogAsync("Reused J", reused);
        var code = ScanData.Upc(6332);
        Products.Know(code, ScanData.Product("Pilotwings 64", code));
        Games.Results.Add(ScanData.Game("Pilotwings 64"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await ScanAsync(editor, sessionId, reused))[0];
        await FirstCandidateAsync(await SubmitBoundAsync(editor, sessionId, Id(row), Code(code)));
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var callsBefore = Products.Calls;
        var nextSession = await StartSessionAsync(editor, library.Id);
        var again = (await ScanAsync(editor, nextSession, code))[0];

        Resolution(again).ShouldBe("matched");
        Products.Calls.ShouldBe(callsBefore, "the committed bound row taught the barcode");
        (await WasLocalHitAsync(Id(again))).ShouldBeTrue();
    }

    [Fact]
    public async Task BindingAnUnknownCandidateIsANotFound()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var response = await SubmitBoundAsync(editor, sessionId, Guid.NewGuid(), Code(ScanData.Upc(6341)));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.candidate_not_found");
    }

    [Fact]
    public async Task ABoundBatchCarriesExactlyOneCode()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await ScanAsync(editor, sessionId, ScanData.Upc(6351)))[0];

        var response = await SubmitBoundAsync(editor, sessionId, Id(row), Code(ScanData.Upc(6352)), Code(ScanData.Upc(6353)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.bound_single_code");
    }

    [Fact]
    public async Task ABoundCodeMustBeReadable()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await ScanAsync(editor, sessionId, ScanData.Upc(6361)))[0];

        var response = await SubmitBoundAsync(editor, sessionId, Id(row), Code("not a barcode"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.code_invalid");
    }

    [Fact]
    public async Task BindingIntoAClosedSessionIsAConflict()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6371);
        Products.Know(code, ScanData.Product("Mario Golf", code));
        Games.Results.Add(ScanData.Game("Mario Golf"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var row = (await ScanAsync(editor, sessionId, code))[0];
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await SubmitBoundAsync(editor, sessionId, Id(row), Code(ScanData.Upc(6372)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.session_closed");
    }
}
