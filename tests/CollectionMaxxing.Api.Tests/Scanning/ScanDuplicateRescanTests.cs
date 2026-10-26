using System.Net;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning;

/// <summary>The duplicate re-scan (#340): re-scanning an owned shelf answers on-card and never adds silently.</summary>
public class ScanDuplicateRescanTests(ApiFixture api) : ScanTest(api)
{
    [Fact]
    public async Task Rescanning_owned_barcodes_creates_no_items_without_an_explicit_add()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf A");
        var code1 = ScanData.Upc(9130);
        var code2 = ScanData.Upc(9131);
        await OwnAsync(library, "Super Mario 64", code1, locationId: shelf);
        await OwnAsync(library, "GoldenEye 007", code2, locationId: shelf);
        (await CountItemsAsync(library.Id)).ShouldBe(2);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        // Every hit is answered as owned on the card; the scan itself adds nothing.
        var candidates = await ScanAsync(editor, sessionId, code1, code2);
        candidates.Count.ShouldBe(2);
        foreach (var candidate in candidates)
        {
            Resolution(candidate).ShouldBe("matched");
            candidate.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        }

        (await CountItemsAsync(library.Id)).ShouldBe(2);

        // Skip both (the owned default): committing afterwards still adds nothing.
        foreach (var candidate in candidates)
        {
            (await DecideAsync(editor, sessionId, Id(candidate), new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountItemsAsync(library.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task Accepting_an_owned_hit_adds_exactly_one_copy_at_commit()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf A");
        var code = ScanData.Upc(9132);
        var (releaseId, _) = await OwnAsync(library, "Super Mario 64", code, locationId: shelf);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);
        (await DecideAsync(editor, sessionId, Id(candidates[0]), new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // "Add another copy" keeps the commit path's semantics: one new Item row.
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(2);
        items.Count(i => i.ReleaseId == releaseId).ShouldBe(2);
    }

    [Fact]
    public async Task Moving_a_copy_relocates_it_without_creating_items()
    {
        var library = await CreateLibraryAsync();
        var shelfA = await TestData.CreateLocationAsync(Db, library.Id, "Shelf A");
        var shelfB = await TestData.CreateLocationAsync(Db, library.Id, "Shelf B");
        var (_, itemId) = await OwnAsync(library, "Super Mario 64", ScanData.Upc(9133), locationId: shelfA);
        using var editor = As(library, LibraryRole.Editor);
        var url = $"/api/v1/libraries/{library.Id}/items/{itemId}";
        var etag = (await editor.GetAsync(url)).Headers.ETag!.Tag;

        // "Move it here" is a location-only patch through the item-update endpoint.
        var moved = await PatchItemAsync(editor, url, etag, new { locationId = shelfB });
        moved.StatusCode.ShouldBe(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
        (await CountItemsAsync(library.Id)).ShouldBe(1);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Single().LocationId.ShouldBe(shelfB);

        // One-tap undo is the same patch back.
        var undone = await PatchItemAsync(editor, url, moved.Headers.ETag!.Tag, new { locationId = shelfA });
        undone.StatusCode.ShouldBe(HttpStatusCode.OK, await undone.Content.ReadAsStringAsync());
        (await CountItemsAsync(library.Id)).ShouldBe(1);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Single().LocationId.ShouldBe(shelfA);
    }

    private static async Task<HttpResponseMessage> PatchItemAsync(HttpClient client, string url, string ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request);
    }
}
