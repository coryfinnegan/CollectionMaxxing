using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Photo;

public class FeaturePrintTests(ApiFixture api) : PhotoTest(api)
{
    private sealed record Print(Guid ItemId, Guid ReleaseId, int Revision, byte[] Data);

    private static StringContent Body(IEnumerable<Print> prints) => Json(new
    {
        prints = prints.Select(p => new { itemId = p.ItemId, releaseId = p.ReleaseId, revision = p.Revision, data = Convert.ToBase64String(p.Data), createdAt = DateTimeOffset.UtcNow }),
    });

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid libraryId, params Print[] prints) =>
        client.PutAsync(FeaturePrintsUrl(libraryId), Body(prints));

    private static async Task<(List<JsonElement> Items, string? Next)> ListAsync(HttpClient client, Guid libraryId, string? query = null)
    {
        var response = await client.GetAsync(FeaturePrintsUrl(libraryId) + (query is null ? string.Empty : "?" + query));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        var next = body.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String ? cursor.GetString() : null;
        return (body.GetProperty("items").EnumerateArray().ToList(), next);
    }

    [Fact]
    public async Task A_put_then_a_get_round_trips_the_opaque_bytes_with_the_server_time()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);
        var releaseId = await TestData.CreateReleaseAsync(Db);
        byte[] data = [0, 1, 2, 250, 251, 252, 253, 254, 255];
        using var editor = As(library, LibraryRole.Editor);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        (await PutAsync(editor, library.Id, new Print(itemId, releaseId, 7, data))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var (items, next) = await ListAsync(editor, library.Id);
        next.ShouldBeNull();
        var print = items.ShouldHaveSingleItem();
        print.GetProperty("itemId").GetGuid().ShouldBe(itemId);
        print.GetProperty("releaseId").GetGuid().ShouldBe(releaseId);
        print.GetProperty("revision").GetInt32().ShouldBe(7);
        Convert.FromBase64String(print.GetProperty("data").GetString()!).ShouldBe(data);
        print.GetProperty("createdAt").GetDateTimeOffset().ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task Since_returns_only_the_rows_written_after_it()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var first = await TestData.CreateItemAsync(Db, library);
        var second = await TestData.CreateItemAsync(Db, library);
        using var editor = As(library, LibraryRole.Editor);
        await PutAsync(editor, library.Id, new Print(first, releaseId, 1, [1]));
        var (all, _) = await ListAsync(editor, library.Id);
        var firstCreatedAt = all.Single().GetProperty("createdAt").GetString()!;
        await Task.Delay(20);
        await PutAsync(editor, library.Id, new Print(second, releaseId, 1, [2]));

        var (newer, _) = await ListAsync(editor, library.Id, "since=" + Uri.EscapeDataString(firstCreatedAt));

        newer.ShouldHaveSingleItem().GetProperty("itemId").GetGuid().ShouldBe(second);
        (await ListAsync(editor, library.Id)).Items.Count.ShouldBe(2);
        (await ListAsync(editor, library.Id, "since=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")))).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_put_of_the_same_item_and_revision_replaces_the_row_and_makes_it_new_again()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);
        var releaseId = await TestData.CreateReleaseAsync(Db);
        using var editor = As(library, LibraryRole.Editor);
        await PutAsync(editor, library.Id, new Print(itemId, releaseId, 1, [1, 1, 1]));
        var firstStamp = (await ListAsync(editor, library.Id)).Items.Single().GetProperty("createdAt").GetString()!;
        await Task.Delay(20);

        (await PutAsync(editor, library.Id, new Print(itemId, releaseId, 1, [2, 2]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using (var context = Db.CreateContext())
        {
            (await context.ItemFeaturePrints.IgnoreQueryFilters().CountAsync(p => p.ItemId == itemId)).ShouldBe(1);
        }

        var (items, _) = await ListAsync(editor, library.Id, "since=" + Uri.EscapeDataString(firstStamp));
        Convert.FromBase64String(items.ShouldHaveSingleItem().GetProperty("data").GetString()!).ShouldBe([2, 2]);
    }

    [Fact]
    public async Task A_new_revision_of_the_same_item_is_a_second_row_and_a_repeat_inside_one_batch_keeps_the_last()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);
        var releaseId = await TestData.CreateReleaseAsync(Db);
        using var editor = As(library, LibraryRole.Editor);

        (await PutAsync(editor, library.Id, new Print(itemId, releaseId, 1, [1]), new Print(itemId, releaseId, 1, [9]), new Print(itemId, releaseId, 2, [3])))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var (items, _) = await ListAsync(editor, library.Id);
        items.Count.ShouldBe(2);
        var byRevision = items.ToDictionary(i => i.GetProperty("revision").GetInt32(), i => Convert.FromBase64String(i.GetProperty("data").GetString()!));
        byRevision[1].ShouldBe([9]);
        byRevision[2].ShouldBe([3]);
    }

    [Fact]
    public async Task Listing_pages_with_a_cursor_and_loses_nothing_when_many_rows_share_one_timestamp()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var itemIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            itemIds.Add(await TestData.CreateItemAsync(Db, library));
        }

        using var editor = As(library, LibraryRole.Editor);
        (await PutAsync(editor, library.Id, itemIds.Select(id => new Print(id, releaseId, 1, [1])).ToArray())).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var (items, next) = await ListAsync(editor, library.Id, "limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            items.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(items.Select(i => i.GetProperty("itemId").GetGuid()));
            cursor = next;
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.ShouldBe(3);
        seen.Order().ToList().ShouldBe(itemIds.Order().ToList());
    }

    [Theory]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=e30")]
    [InlineData("since=yesterday")]
    public async Task A_bad_cursor_or_since_is_a_400(string query)
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);

        var response = await viewer.GetAsync(FeaturePrintsUrl(library.Id) + "?" + query);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task A_batch_of_200_is_accepted_and_201_is_refused_with_nothing_written()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var itemId = await TestData.CreateItemAsync(Db, library);
        using var editor = As(library, LibraryRole.Editor);

        // Revisions 1..200 of one item are 200 distinct rows.
        var batch = Enumerable.Range(1, 200).Select(r => new Print(itemId, releaseId, r, [1])).ToArray();
        (await PutAsync(editor, library.Id, batch)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var tooMany = Enumerable.Range(1, 201).Select(r => new Print(itemId, releaseId, 1000 + r, [1])).ToArray();

        var refused = await PutAsync(editor, library.Id, tooMany);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var context = Db.CreateContext();
        (await context.ItemFeaturePrints.IgnoreQueryFilters().CountAsync(p => p.LibraryId == library.Id)).ShouldBe(200);
    }

    [Fact]
    public async Task A_print_of_16_kilobytes_is_accepted_and_one_byte_more_is_refused()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var itemId = await TestData.CreateItemAsync(Db, library);
        using var editor = As(library, LibraryRole.Editor);

        (await PutAsync(editor, library.Id, new Print(itemId, releaseId, 1, new byte[16 * 1024]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var refused = await PutAsync(editor, library.Id, new Print(itemId, releaseId, 2, new byte[(16 * 1024) + 1]));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ListAsync(editor, library.Id)).Items.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("""{ "prints": [ { "itemId": "ITEM", "releaseId": "RELEASE", "revision": 1, "data": "not base64 !!" } ] }""")]
    [InlineData("""{ "prints": [ { "itemId": "ITEM", "releaseId": "RELEASE", "revision": 1, "data": "" } ] }""")]
    [InlineData("""{ "prints": [ { "itemId": "ITEM", "releaseId": "RELEASE", "revision": 1 } ] }""")]
    [InlineData("""{ "prints": [ { "itemId": "ITEM", "releaseId": "RELEASE", "revision": 0, "data": "AQID" } ] }""")]
    [InlineData("""{ "prints": [ { "itemId": "ITEM", "releaseId": "RELEASE", "revision": -3, "data": "AQID" } ] }""")]
    [InlineData("""{ "prints": [ { "releaseId": "RELEASE", "revision": 1, "data": "AQID" } ] }""")]
    [InlineData("""{ "prints": [ { "itemId": "ITEM", "revision": 1, "data": "AQID" } ] }""")]
    [InlineData("""{ "prints": [ null ] }""")]
    [InlineData("""{ "prints": [] }""")]
    [InlineData("""{}""")]
    public async Task A_malformed_batch_is_a_400_and_writes_nothing(string json)
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var itemId = await TestData.CreateItemAsync(Db, library);
        using var editor = As(library, LibraryRole.Editor);
        var body = json.Replace("ITEM", itemId.ToString(), StringComparison.Ordinal).Replace("RELEASE", releaseId.ToString(), StringComparison.Ordinal);

        var response = await editor.PutAsync(FeaturePrintsUrl(library.Id), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await ListAsync(editor, library.Id)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deleted_item_cannot_be_given_a_print()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var itemId = await TestData.CreateItemAsync(Db, library);
        await using (var context = Db.CreateContext())
        {
            await context.Items.IgnoreQueryFilters().Where(i => i.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedAt, DateTimeOffset.UtcNow));
        }

        using var editor = As(library, LibraryRole.Editor);

        (await PutAsync(editor, library.Id, new Print(itemId, releaseId, 1, [1]))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_member_of_two_libraries_cannot_file_a_print_under_one_for_an_item_of_the_other()
    {
        var libraryA = await CreateLibraryAsync();
        var libraryB = await CreateLibraryAsync();
        await using (var context = Db.CreateContext())
        {
            context.LibraryMembers.Add(new LibraryMember(libraryB.Id, libraryA.EditorId, LibraryRole.Editor, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var releaseId = await TestData.CreateReleaseAsync(Db);
        var itemOfB = await TestData.CreateItemAsync(Db, libraryB);
        using var editor = As(libraryA, LibraryRole.Editor);

        (await PutAsync(editor, libraryA.Id, new Print(itemOfB, releaseId, 1, [1]))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var check = Db.CreateContext();
        (await check.ItemFeaturePrints.IgnoreQueryFilters().CountAsync(p => p.ItemId == itemOfB)).ShouldBe(0);
    }

    [Fact]
    public async Task A_member_of_two_libraries_cannot_use_the_private_release_of_the_other()
    {
        var libraryA = await CreateLibraryAsync();
        var libraryB = await CreateLibraryAsync();
        await using (var context = Db.CreateContext())
        {
            context.LibraryMembers.Add(new LibraryMember(libraryB.Id, libraryA.EditorId, LibraryRole.Editor, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var releaseOfB = await TestData.CreateReleaseAsync(Db, libraryB.Id);
        var itemOfA = await TestData.CreateItemAsync(Db, libraryA);
        using var editor = As(libraryA, LibraryRole.Editor);

        (await PutAsync(editor, libraryA.Id, new Print(itemOfA, releaseOfB, 1, [1]))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_print_goes_when_its_item_is_deleted_for_good()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var itemId = await TestData.CreateItemAsync(Db, library);
        using var editor = As(library, LibraryRole.Editor);
        await PutAsync(editor, library.Id, new Print(itemId, releaseId, 1, [1]));

        await using (var context = Db.CreateContext())
        {
            await context.Items.IgnoreQueryFilters().Where(i => i.Id == itemId).ExecuteDeleteAsync();
            (await context.ItemFeaturePrints.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_library_private_release_of_the_same_library_can_be_used()
    {
        var library = await CreateLibraryAsync();
        var ownRelease = await TestData.CreateReleaseAsync(Db, library.Id);
        var itemId = await TestData.CreateItemAsync(Db, library, ownRelease);
        using var editor = As(library, LibraryRole.Editor);

        (await PutAsync(editor, library.Id, new Print(itemId, ownRelease, 1, [1]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
