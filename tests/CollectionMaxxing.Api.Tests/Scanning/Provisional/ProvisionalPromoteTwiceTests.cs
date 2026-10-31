using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

// A kept row becomes one item however many times, or from however many tabs, the person taps Promote.
public class ProvisionalPromoteTwiceTests(ApiFixture api) : ProvisionalTest(api)
{
    private static Task<HttpResponseMessage> PromoteAsync(HttpClient client, Guid libraryId, Guid provisionalId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ProvisionalUrl(libraryId, provisionalId)}:promote")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private async Task<(TestLibrary Library, HttpClient Editor, Guid ProvisionalId)> KeptAsync()
    {
        var library = await CreateLibraryAsync();
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var kept = await KeepAsync(editor, sessionId, new { candidateId });
        kept.StatusCode.ShouldBe(HttpStatusCode.Created, await kept.Content.ReadAsStringAsync());
        return (library, editor, (await ReadJsonAsync(kept)).GetProperty("provisional").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Promoting_with_a_new_key_after_the_first_promote_returns_the_same_item()
    {
        var (library, editor, provisionalId) = await KeptAsync();
        using var client = editor;

        var first = await PromoteAsync(editor, library.Id, provisionalId, Guid.NewGuid().ToString());
        var second = await PromoteAsync(editor, library.Id, provisionalId, Guid.NewGuid().ToString());

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        (await ReadJsonAsync(second)).GetProperty("createdItemId").GetGuid()
            .ShouldBe((await ReadJsonAsync(first)).GetProperty("createdItemId").GetGuid());
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Promoting_at_the_same_moment_with_different_keys_makes_one_item_and_no_orphan()
    {
        var (library, editor, provisionalId) = await KeptAsync();
        using var client = editor;

        // A double tap, or two tabs: each request has its own Idempotency-Key, so the key alone can't tell them apart.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PromoteAsync(editor, library.Id, provisionalId, Guid.NewGuid().ToString())));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        statuses.ShouldAllBe(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict, string.Join(", ", statuses));
        statuses.ShouldContain(HttpStatusCode.OK);
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        var item = items.ShouldHaveSingleItem("one kept row is one item, and the request that lost leaves nothing behind");
        var stored = (await ProvisionalAsync(provisionalId)).ShouldNotBeNull();
        stored.ResolveAction.ShouldBe(ProvisionalResolveAction.Promoted);
        stored.CreatedItemId.ShouldBe(item.Id);
    }
}
