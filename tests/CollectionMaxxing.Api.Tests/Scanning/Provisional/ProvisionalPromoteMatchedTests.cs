using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

// Issue #346 (design §11): a kept row whose barcode was scanned afterwards promotes to the resolved release —
// verified — instead of a manual record typed from the guess.
public class ProvisionalPromoteMatchedTests(ApiFixture api) : ProvisionalTest(api)
{
    private static async Task<Guid> ProvisionalIdOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("provisional").GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PromoteAsync(HttpClient client, Guid libraryId, Guid provisionalId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ProvisionalUrl(libraryId, provisionalId)}:promote")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SubmitBoundAsync(HttpClient client, Guid sessionId, Guid candidateId, string code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/barcodes")
        {
            Content = Json(new { codes = new[] { new { value = code, symbology = "ean_13" } }, candidateId }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Promoting_a_row_whose_barcode_resolved_uses_that_release_verified()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(64601);
        var releaseId = await CatalogAsync("Wave Race 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var provisionalId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId }));

        var bound = await SubmitBoundAsync(editor, sessionId, candidateId, code);
        bound.StatusCode.ShouldBe(HttpStatusCode.OK, await bound.Content.ReadAsStringAsync());
        var response = await PromoteAsync(editor, library.Id, provisionalId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var itemId = (await ReadJsonAsync(response)).GetProperty("createdItemId").GetGuid();
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        var item = items.ShouldHaveSingleItem();
        item.Id.ShouldBe(itemId);
        item.ReleaseId.ShouldBe(releaseId, "the barcode named the edition; the guess is not retyped as a manual record");
        item.EditionVerified.ShouldBeTrue();
        (await CandidateAsync(candidateId)).CreatedItemId.ShouldBe(itemId);
    }

    [Fact]
    public async Task Promote_ignores_a_match_the_library_cannot_use_and_falls_back_to_the_guess()
    {
        var library = await CreateLibraryAsync();
        var foreign = await CreateLibraryAsync();
        var privateRelease = await ItemSeed.AddReleaseAsync(Db, "Someone Else's Tape", ownerLibraryId: foreign.Id);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId, "Zzxq Guess Qqz"))[0]);
        var provisionalId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId }));
        await using (var context = Db.CreateContext())
        {
            var row = await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == candidateId);
            row.MatchedReleaseId = privateRelease;
            await context.SaveChangesAsync();
        }

        var response = await PromoteAsync(editor, library.Id, provisionalId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var item = (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldHaveSingleItem();
        item.ReleaseId.ShouldNotBe(privateRelease);
        await using var check = Db.CreateContext();
        var release = await check.Releases.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == item.ReleaseId);
        release.Source.ShouldBe(ReleaseSource.User);
        release.OwnerLibraryId.ShouldBe(library.Id);
    }
}
