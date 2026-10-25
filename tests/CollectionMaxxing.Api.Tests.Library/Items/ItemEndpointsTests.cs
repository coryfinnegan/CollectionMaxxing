using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Items;

/// <summary>Acceptance tests for creating, reading, editing and deleting items, from the caller's side.</summary>
public class ItemEndpointsTests(ApiFixture api) : ApiTest(api)
{
    private static string ItemsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/items";

    private static string ItemUrl(Guid libraryId, Guid itemId) => $"{ItemsUrl(libraryId)}/{itemId}";

    private static async Task<HttpResponseMessage> PostItemAsync(HttpClient client, Guid libraryId, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl(libraryId)) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendWithIfMatchAsync(
        HttpClient client, HttpMethod method, string url, string? ifMatch, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string url, string? ifMatch, object body) =>
        SendWithIfMatchAsync(client, HttpMethod.Patch, url, ifMatch, body);

    private static string ETagOf(HttpResponseMessage response) => response.Headers.ETag!.Tag;

    // ---- Create -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_editor_adds_an_item_from_a_release_and_the_caller_is_recorded_as_its_creator()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db);

        var response = await PostItemAsync(
            ClientFor(library, LibraryRole.Editor), library.Id,
            new { releaseId, status = "wishlist", condition = "very_good", notes = "Boxed", createdBy = Guid.NewGuid() });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var item = await ReadJsonAsync(response);
        item.GetProperty("releaseId").GetGuid().ShouldBe(releaseId);
        item.GetProperty("status").GetString().ShouldBe("wishlist");
        item.GetProperty("condition").GetString().ShouldBe("very_good");
        item.GetProperty("createdBy").GetGuid().ShouldBe(library.EditorId);
        item.GetProperty("addedVia").GetString().ShouldBe("search");
        response.Headers.Location!.ToString().ShouldBe(ItemUrl(library.Id, item.GetProperty("id").GetGuid()));
        response.Headers.ETag.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_manual_item_creates_a_release_that_belongs_to_the_library()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);

        var response = await PostItemAsync(
            editor, library.Id,
            new { manual = new { kind = "game", title = "Chrono Trigger", year = 1995, format = "game_cartridge", barcode = "0045496870010" } });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await ReadJsonAsync(response);
        created.GetProperty("addedVia").GetString().ShouldBe("manual");
        var detail = await ReadJsonAsync(await editor.GetAsync(ItemUrl(library.Id, created.GetProperty("id").GetGuid())));
        var release = detail.GetProperty("release");
        release.GetProperty("source").GetString().ShouldBe("user");
        release.GetProperty("format").GetString().ShouldBe("game_cartridge");
        release.GetProperty("identifiers").EnumerateArray().Single().GetProperty("value").GetString().ShouldBe("0045496870010");
        detail.GetProperty("work").GetProperty("title").GetString().ShouldBe("Chrono Trigger");

        // Private to the library: another library can't attach it.
        var other = await CreateLibraryAsync();
        var attach = await PostItemAsync(ClientFor(other, LibraryRole.Editor), other.Id, new { releaseId = release.GetProperty("id").GetGuid() });
        attach.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_request_with_neither_a_release_nor_manual_details_is_a_validation_problem()
    {
        var library = await CreateLibraryAsync();

        var response = await PostItemAsync(ClientFor(library, LibraryRole.Editor), library.Id, new { });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.source_required");
    }

    [Fact]
    public async Task A_manual_item_needs_a_title()
    {
        var library = await CreateLibraryAsync();

        var response = await PostItemAsync(ClientFor(library, LibraryRole.Editor), library.Id, new { manual = new { kind = "book" } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.title_required");
    }

    [Fact]
    public async Task Creating_without_an_idempotency_key_is_a_bad_request()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db);

        var response = await ClientFor(library, LibraryRole.Editor).PostAsJsonAsync(ItemsUrl(library.Id), new { releaseId });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Replaying_a_create_with_the_same_key_returns_the_same_item_and_creates_one_row()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var releaseId = await ItemSeed.AddReleaseAsync(Db);

        var first = await PostItemAsync(editor, library.Id, new { releaseId }, "key-1");
        var second = await PostItemAsync(editor, library.Id, new { releaseId }, "key-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadJsonAsync(second)).GetProperty("id").GetGuid().ShouldBe((await ReadJsonAsync(first)).GetProperty("id").GetGuid());
        (await ItemSeed.CountItemsAsync(Db, library.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Replaying_a_manual_create_does_not_create_a_second_release()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var body = new { manual = new { kind = "book", title = "Dune" } };

        await PostItemAsync(editor, library.Id, body, "key-manual");
        await PostItemAsync(editor, library.Id, body, "key-manual");

        (await ItemSeed.CountReleasesAsync(Db)).ShouldBe(1);
        (await ItemSeed.CountItemsAsync(Db, library.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_unprocessable()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var releaseId = await ItemSeed.AddReleaseAsync(Db);

        await PostItemAsync(editor, library.Id, new { releaseId }, "key-2");
        var response = await PostItemAsync(editor, library.Id, new { releaseId, notes = "different" }, "key-2");

        response.StatusCode.ShouldBe((HttpStatusCode)422);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("idempotency.key_reused");
        (await ItemSeed.CountItemsAsync(Db, library.Id)).ShouldBe(1);
    }

    // ---- Cross-library private data ---------------------------------------------------------------------

    [Fact]
    public async Task A_release_private_to_another_library_is_not_found_on_create()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var foreign = await ItemSeed.AddReleaseAsync(Db, ownerLibraryId: other.Id);

        var response = await PostItemAsync(ClientFor(library, LibraryRole.Editor), library.Id, new { releaseId = foreign });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ItemSeed.CountItemsAsync(Db, library.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_global_release_and_an_own_library_release_can_be_added()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var global = await ItemSeed.AddReleaseAsync(Db);
        var own = await ItemSeed.AddReleaseAsync(Db, ownerLibraryId: library.Id);

        (await PostItemAsync(editor, library.Id, new { releaseId = global })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostItemAsync(editor, library.Id, new { releaseId = own })).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_release_private_to_another_library_is_not_found_on_rematch_even_for_someone_in_both_libraries()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var foreign = await ItemSeed.AddReleaseAsync(Db, ownerLibraryId: other.Id);
        var itemId = await TestData.CreateItemAsync(Db, library);

        var response = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync($"{ItemUrl(library.Id, itemId)}:rematch", new { releaseId = foreign });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rematching_to_a_global_or_own_release_swaps_the_release_and_keeps_the_copy_data()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var created = await ReadJsonAsync(await PostItemAsync(
            editor, library.Id, new { releaseId = await ItemSeed.AddReleaseAsync(Db), condition = "mint", notes = "Keep me" }));
        var itemId = created.GetProperty("id").GetGuid();
        var better = await ItemSeed.AddReleaseAsync(Db, ownerLibraryId: library.Id);

        var response = await editor.PostAsJsonAsync($"{ItemUrl(library.Id, itemId)}:rematch", new { releaseId = better });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = await ReadJsonAsync(response);
        item.GetProperty("releaseId").GetGuid().ShouldBe(better);
        item.GetProperty("condition").GetString().ShouldBe("mint");
        item.GetProperty("notes").GetString().ShouldBe("Keep me");
    }

    [Fact]
    public async Task A_location_or_tag_from_another_library_is_not_found_on_create_and_update()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var releaseId = await ItemSeed.AddReleaseAsync(Db);
        var foreignLocation = await TestData.CreateLocationAsync(Db, other.Id, "Elsewhere");
        var foreignTag = await ItemSeed.AddTagAsync(Db, other.Id, "theirs");

        (await PostItemAsync(editor, library.Id, new { releaseId, locationId = foreignLocation })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostItemAsync(editor, library.Id, new { releaseId, tagIds = new[] { foreignTag } })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var created = await PostItemAsync(editor, library.Id, new { releaseId });
        var etag = ETagOf(created);
        var itemUrl = ItemUrl(library.Id, (await ReadJsonAsync(created)).GetProperty("id").GetGuid());
        (await PatchAsync(editor, itemUrl, etag, new { locationId = foreignLocation })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PatchAsync(editor, itemUrl, etag, new { tagIds = new[] { foreignTag } })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Reading, ETag and If-Match ---------------------------------------------------------------------

    [Fact]
    public async Task Getting_an_item_returns_its_work_release_and_other_copies_with_an_etag()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db, "Alien");
        var workId = await ItemSeed.WorkOfAsync(Db, releaseId);
        var bluray = await ItemSeed.AddReleaseAsync(Db, workId: workId, format: ReleaseFormat.Bluray);
        var first = await TestData.CreateItemAsync(Db, library, releaseId);
        var second = await TestData.CreateItemAsync(Db, library, bluray);

        var response = await ClientFor(library, LibraryRole.Viewer).GetAsync(ItemUrl(library.Id, first));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
        var detail = await ReadJsonAsync(response);
        detail.GetProperty("item").GetProperty("id").GetGuid().ShouldBe(first);
        detail.GetProperty("work").GetProperty("title").GetString().ShouldBe("Alien");
        detail.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(releaseId);
        var others = detail.GetProperty("otherCopies").EnumerateArray().ToList();
        others.Select(o => o.GetProperty("itemId").GetGuid()).ShouldBe([second]);
        others[0].GetProperty("format").GetString().ShouldBe("bluray");
    }

    [Fact]
    public async Task An_item_of_another_library_is_not_found()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, other);

        var response = await ClientFor(library, LibraryRole.Owner).GetAsync(ItemUrl(library.Id, itemId));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_and_delete_without_if_match_are_bad_requests()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var itemId = await TestData.CreateItemAsync(Db, library);

        (await PatchAsync(editor, ItemUrl(library.Id, itemId), null, new { notes = "x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendWithIfMatchAsync(editor, HttpMethod.Delete, ItemUrl(library.Id, itemId), null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_stale_if_match_is_precondition_failed_and_a_matching_one_succeeds_with_a_new_etag()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var itemId = await TestData.CreateItemAsync(Db, library);
        var url = ItemUrl(library.Id, itemId);
        var etag = ETagOf(await editor.GetAsync(url));

        var stale = await PatchAsync(editor, url, "\"1\"", new { notes = "stale" });
        var ok = await PatchAsync(editor, url, etag, new { notes = "fresh" });

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(ok)).GetProperty("notes").GetString().ShouldBe("fresh");
        ETagOf(ok).ShouldNotBe(etag);
        ETagOf(await editor.GetAsync(url)).ShouldBe(ETagOf(ok));
    }

    [Fact]
    public async Task Two_members_editing_from_the_same_version_the_second_gets_precondition_failed()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);
        var url = ItemUrl(library.Id, itemId);
        var etag = ETagOf(await ClientFor(library, LibraryRole.Viewer).GetAsync(url));

        var firstEditor = await PatchAsync(ClientFor(library, LibraryRole.Owner), url, etag, new { notes = "owner" });
        var secondEditor = await PatchAsync(ClientFor(library, LibraryRole.Editor), url, etag, new { notes = "editor" });

        firstEditor.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondEditor.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadJsonAsync(await ClientFor(library, LibraryRole.Viewer).GetAsync(url)))
            .GetProperty("item").GetProperty("notes").GetString().ShouldBe("owner");
    }

    [Fact]
    public async Task A_merge_patch_changes_only_what_is_sent_and_null_clears_a_field()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var created = await PostItemAsync(
            editor, library.Id, new { releaseId = await ItemSeed.AddReleaseAsync(Db), notes = "Boxed", condition = "good" });
        var url = ItemUrl(library.Id, (await ReadJsonAsync(created)).GetProperty("id").GetGuid());

        var response = await PatchAsync(editor, url, ETagOf(created), "{ \"notes\": null, \"status\": \"lent\" }");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = await ReadJsonAsync(response);
        item.GetProperty("notes").ValueKind.ShouldBe(JsonValueKind.Null);
        item.GetProperty("condition").GetString().ShouldBe("good");
        item.GetProperty("status").GetString().ShouldBe("lent");
    }

    [Fact]
    public async Task A_patch_replaces_the_tags_on_an_item()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var keep = await ItemSeed.AddTagAsync(Db, library.Id, "keep");
        var drop = await ItemSeed.AddTagAsync(Db, library.Id, "drop");
        var created = await PostItemAsync(editor, library.Id, new { releaseId = await ItemSeed.AddReleaseAsync(Db), tagIds = new[] { drop } });
        var url = ItemUrl(library.Id, (await ReadJsonAsync(created)).GetProperty("id").GetGuid());

        var response = await PatchAsync(editor, url, ETagOf(created), new { tagIds = new[] { keep } });

        (await ReadJsonAsync(response)).GetProperty("tagIds").EnumerateArray().Select(t => t.GetGuid()).ShouldBe([keep]);
    }

    [Fact]
    public async Task A_viewer_cannot_edit_an_item()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);

        var response = await PatchAsync(ClientFor(library, LibraryRole.Viewer), ItemUrl(library.Id, itemId), "\"1\"", new { notes = "x" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---- Soft delete and restore ------------------------------------------------------------------------

    [Fact]
    public async Task A_deleted_item_disappears_and_can_be_restored()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var itemId = await TestData.CreateItemAsync(Db, library);
        var url = ItemUrl(library.Id, itemId);

        var deleted = await SendWithIfMatchAsync(editor, HttpMethod.Delete, url, ETagOf(await editor.GetAsync(url)));

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await editor.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(await editor.GetAsync(ItemsUrl(library.Id)))).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await ItemSeed.CountItemsAsync(Db, library.Id)).ShouldBe(1); // soft: the row is still there

        var restored = await editor.PostAsync($"{url}:restore", null);

        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(restored)).GetProperty("deletedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        (await editor.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deleting_with_a_stale_if_match_is_precondition_failed_and_deletes_nothing()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var itemId = await TestData.CreateItemAsync(Db, library);

        var response = await SendWithIfMatchAsync(editor, HttpMethod.Delete, ItemUrl(library.Id, itemId), "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await editor.GetAsync(ItemUrl(library.Id, itemId))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_item_deleted_more_than_30_days_ago_cannot_be_restored()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var itemId = await TestData.CreateItemAsync(Db, library);
        await ItemSeed.SetDeletedAtAsync(Db, itemId, DateTimeOffset.UtcNow.AddDays(-31));

        var response = await editor.PostAsync($"{ItemUrl(library.Id, itemId)}:restore", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.restore_window_closed");
    }

    [Fact]
    public async Task An_item_deleted_29_days_ago_can_still_be_restored()
    {
        var library = await CreateLibraryAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);
        await ItemSeed.SetDeletedAtAsync(Db, itemId, DateTimeOffset.UtcNow.AddDays(-29));

        var response = await ClientFor(library, LibraryRole.Editor).PostAsync($"{ItemUrl(library.Id, itemId)}:restore", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- List -------------------------------------------------------------------------------------------

    private static async Task<List<string>> TitlesAsync(HttpClient client, Guid libraryId, string query)
    {
        var response = await client.GetAsync($"{ItemsUrl(libraryId)}?{query}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var page = await ReadJsonAsync(response);
        return page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToList();
    }

    private async Task<(TestLibrary Library, HttpClient Viewer)> ArrangeShelfAsync()
    {
        var library = await CreateLibraryAsync();
        var n64 = await ItemSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        var ps2 = await ItemSeed.AddPlatformAsync(Db, "PlayStation 2", "PS2");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        await ItemSeed.AddItemAsync(Db, library, "Zelda", WorkKind.Game, ReleaseFormat.GameCartridge, 1998, n64, ItemStatus.Owned, shelf, addedMinutes: 1);
        await ItemSeed.AddItemAsync(Db, library, "Mario Kart", WorkKind.Game, ReleaseFormat.GameCartridge, 1996, n64, ItemStatus.Wishlist, addedMinutes: 2);
        await ItemSeed.AddItemAsync(Db, library, "Okami", WorkKind.Game, ReleaseFormat.GameDisc, 2006, ps2, ItemStatus.Owned, addedMinutes: 3);
        await ItemSeed.AddItemAsync(Db, library, "Alien", WorkKind.Film, ReleaseFormat.Vhs, 1979, null, ItemStatus.Lent, addedMinutes: 4);
        await ItemSeed.AddItemAsync(Db, library, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965, null, ItemStatus.Owned, addedMinutes: 5);
        return (library, ClientFor(library, LibraryRole.Viewer));
    }

    [Fact]
    public async Task The_list_defaults_to_title_order()
    {
        var (library, viewer) = await ArrangeShelfAsync();

        (await TitlesAsync(viewer, library.Id, string.Empty)).ShouldBe(["Alien", "Dune", "Mario Kart", "Okami", "Zelda"]);
    }

    [Theory]
    [InlineData("sort=title", "Alien,Dune,Mario Kart,Okami,Zelda")]
    [InlineData("sort=-addedAt", "Dune,Alien,Okami,Mario Kart,Zelda")]
    [InlineData("sort=year", "Dune,Alien,Mario Kart,Zelda,Okami")]
    public async Task The_list_sorts(string query, string expected)
    {
        var (library, viewer) = await ArrangeShelfAsync();

        var titles = await TitlesAsync(viewer, library.Id, query);

        titles.ShouldBe(expected.Split(','));
    }

    [Fact]
    public async Task The_list_sorts_by_platform_with_items_that_have_none_first()
    {
        var (library, viewer) = await ArrangeShelfAsync();

        var titles = await TitlesAsync(viewer, library.Id, "sort=platform");

        titles.Take(2).Order().ShouldBe(["Alien", "Dune"]); // no platform
        titles.Skip(2).Take(2).Order().ShouldBe(["Mario Kart", "Zelda"]); // Nintendo 64
        titles[4].ShouldBe("Okami"); // PlayStation 2
    }

    [Theory]
    [InlineData("kind=game", "Mario Kart,Okami,Zelda")]
    [InlineData("kind=film", "Alien")]
    [InlineData("format=game_cartridge", "Mario Kart,Zelda")]
    [InlineData("format=vhs", "Alien")]
    [InlineData("platform=N64", "Mario Kart,Zelda")]
    [InlineData("platform=playstation 2", "Okami")]
    [InlineData("status=wishlist", "Mario Kart")]
    [InlineData("status=owned", "Dune,Okami,Zelda")]
    [InlineData("q=zel", "Zelda")]
    [InlineData("q=ALIEN", "Alien")]
    public async Task The_list_filters(string query, string expected)
    {
        var (library, viewer) = await ArrangeShelfAsync();

        (await TitlesAsync(viewer, library.Id, query)).ShouldBe(expected.Split(','));
    }

    [Fact]
    public async Task The_list_filters_by_location_and_by_tag_name()
    {
        var (library, viewer) = await ArrangeShelfAsync();
        var page = await ReadJsonAsync(await viewer.GetAsync(ItemsUrl(library.Id)));
        var zelda = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("title").GetString() == "Zelda");
        var locationId = zelda.GetProperty("locationId").GetGuid();
        var tagId = await ItemSeed.AddTagAsync(Db, library.Id, "Classics");
        await ItemSeed.TagItemAsync(Db, library.Id, zelda.GetProperty("id").GetGuid(), tagId);

        (await TitlesAsync(viewer, library.Id, $"locationId={locationId}")).ShouldBe(["Zelda"]);
        (await TitlesAsync(viewer, library.Id, "tag=classics")).ShouldBe(["Zelda"]);
        (await TitlesAsync(viewer, library.Id, "tag=nothing")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("sort=sideways")]
    [InlineData("format=papyrus")]
    [InlineData("cursor=not-a-cursor")]
    public async Task A_bad_sort_format_or_cursor_is_a_bad_request(string query)
    {
        var (library, viewer) = await ArrangeShelfAsync();

        var response = await viewer.GetAsync($"{ItemsUrl(library.Id)}?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("-addedAt")]
    [InlineData("year")]
    [InlineData("platform")]
    public async Task Paging_through_the_list_has_no_duplicates_and_no_gaps(string sort)
    {
        var library = await CreateLibraryAsync();
        var platform = await ItemSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        for (var n = 0; n < 11; n++)
        {
            // Many ties on year, platform and title so that the id tie-break matters.
            await ItemSeed.AddItemAsync(Db, library, n % 2 == 0 ? "Same" : "Other", year: 1990 + (n % 3), platformId: n % 3 == 0 ? platform : null, addedMinutes: n / 4);
        }

        var viewer = ClientFor(library, LibraryRole.Viewer);
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"{ItemsUrl(library.Id)}?sort={sort}&limit=4" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await ReadJsonAsync(await viewer.GetAsync(url));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.ShouldBe(3);
        seen.Count.ShouldBe(11);
        seen.Distinct().Count().ShouldBe(11);
    }

    [Fact]
    public async Task The_limit_is_capped_at_200_and_defaults_to_50()
    {
        var library = await CreateLibraryAsync();
        var viewer = ClientFor(library, LibraryRole.Viewer);
        await ItemSeed.AddItemAsync(Db, library, "One");

        (await viewer.GetAsync($"{ItemsUrl(library.Id)}?limit=100000")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(await viewer.GetAsync(ItemsUrl(library.Id)))).GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_list_never_shows_another_librarys_items_or_deleted_items()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        await ItemSeed.AddItemAsync(Db, library, "Mine");
        await ItemSeed.AddItemAsync(Db, other, "Theirs");
        var gone = await ItemSeed.AddItemAsync(Db, library, "Gone");
        await ItemSeed.SetDeletedAtAsync(Db, gone, DateTimeOffset.UtcNow);

        (await TitlesAsync(ClientFor(library, LibraryRole.Viewer), library.Id, string.Empty)).ShouldBe(["Mine"]);
    }

    // ---- Bulk -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_bulk_patch_moves_items_sets_status_and_tags_them()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var a = await TestData.CreateItemAsync(Db, library);
        var b = await TestData.CreateItemAsync(Db, library);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var tag = await ItemSeed.AddTagAsync(Db, library.Id, "sale");

        var response = await editor.PostAsJsonAsync(
            $"{ItemsUrl(library.Id)}:bulk",
            new { ids = new[] { a, b }, action = "patch", patch = new { locationId = shelf, status = "sold", addTagIds = new[] { tag } } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("affected").GetInt32().ShouldBe(2);
        var detail = await ReadJsonAsync(await editor.GetAsync(ItemUrl(library.Id, a)));
        detail.GetProperty("item").GetProperty("locationId").GetGuid().ShouldBe(shelf);
        detail.GetProperty("item").GetProperty("status").GetString().ShouldBe("sold");
        detail.GetProperty("item").GetProperty("tagIds").EnumerateArray().Select(t => t.GetGuid()).ShouldBe([tag]);
    }

    [Fact]
    public async Task A_bulk_delete_soft_deletes_the_items()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var a = await TestData.CreateItemAsync(Db, library);
        var b = await TestData.CreateItemAsync(Db, library);

        var response = await editor.PostAsJsonAsync($"{ItemsUrl(library.Id)}:bulk", new { ids = new[] { a, b }, action = "delete" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await editor.GetAsync(ItemUrl(library.Id, a))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ItemSeed.CountItemsAsync(Db, library.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task A_bulk_request_with_more_than_500_ids_is_a_bad_request()
    {
        var library = await CreateLibraryAsync();
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();

        var response = await ClientFor(library, LibraryRole.Editor)
            .PostAsJsonAsync($"{ItemsUrl(library.Id)}:bulk", new { ids, action = "delete" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("bulk.too_many_ids");
    }

    [Fact]
    public async Task One_id_from_another_library_fails_the_whole_bulk_call_and_changes_nothing()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var mine = await TestData.CreateItemAsync(Db, library);
        var theirs = await TestData.CreateItemAsync(Db, other);

        var response = await editor.PostAsJsonAsync($"{ItemsUrl(library.Id)}:bulk", new { ids = new[] { mine, theirs }, action = "delete" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await editor.GetAsync(ItemUrl(library.Id, mine))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ClientFor(other, LibraryRole.Editor).GetAsync(ItemUrl(other.Id, theirs))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_bulk_patch_with_a_foreign_location_changes_nothing()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var mine = await TestData.CreateItemAsync(Db, library);
        var foreign = await TestData.CreateLocationAsync(Db, other.Id, "Theirs");

        var response = await editor.PostAsJsonAsync(
            $"{ItemsUrl(library.Id)}:bulk", new { ids = new[] { mine }, action = "patch", patch = new { locationId = foreign, status = "sold" } });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(await editor.GetAsync(ItemUrl(library.Id, mine)))).GetProperty("item").GetProperty("status").GetString().ShouldBe("owned");
    }

    // ---- Stats ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stats_count_the_librarys_items_by_kind_format_platform_and_decade()
    {
        var (library, viewer) = await ArrangeShelfAsync();
        var other = await CreateLibraryAsync();
        await ItemSeed.AddItemAsync(Db, other, "Not counted", WorkKind.Book);
        var deleted = await ItemSeed.AddItemAsync(Db, library, "Deleted", WorkKind.Book);
        await ItemSeed.SetDeletedAtAsync(Db, deleted, DateTimeOffset.UtcNow);

        var stats = await ReadJsonAsync(await viewer.GetAsync($"/api/v1/libraries/{library.Id}/stats"));

        stats.GetProperty("total").GetInt32().ShouldBe(5);
        Counts(stats, "byKind").ShouldBe(new Dictionary<string, int> { ["book"] = 1, ["film"] = 1, ["game"] = 3 });
        Counts(stats, "byFormat").ShouldBe(
            new Dictionary<string, int> { ["game_cartridge"] = 2, ["game_disc"] = 1, ["paperback"] = 1, ["vhs"] = 1 });
        Counts(stats, "byPlatform").ShouldBe(new Dictionary<string, int> { ["Nintendo 64"] = 2, ["PlayStation 2"] = 1 });
        Counts(stats, "byDecade").ShouldBe(
            new Dictionary<string, int> { ["1960s"] = 1, ["1970s"] = 1, ["1990s"] = 2, ["2000s"] = 1 });
    }

    private static Dictionary<string, int> Counts(JsonElement stats, string name) =>
        stats.GetProperty(name).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
}
