using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

/// <summary>Keeping an unreadable spine as a provisional record and working through it (#345, design §11).</summary>
public class ProvisionalLifecycleTests : ProvisionalTest
{
    public ProvisionalLifecycleTests(ApiFixture api)
        : base(api)
    {
    }

    [Fact]
    public async Task Keeping_a_t2_row_makes_an_unverified_provisional_and_retires_the_row()
    {
        var library = await CreateLibraryAsync();
        var locationId = await TestData.CreateLocationAsync(Db, library.Id, "Shelf A");
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { defaultLocationId = locationId, mediaHint = "books" });
        var candidate = (await IdentifyUnreadableAsync(editor, sessionId))[0];
        var candidateId = Id(candidate);
        var cropId = await UploadCropIdAsync(editor, sessionId, candidateId);

        var response = await KeepAsync(editor, sessionId, new { candidateId, cropAssetId = cropId });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        var kept = body.GetProperty("provisional");
        var provisionalId = kept.GetProperty("id").GetGuid();
        kept.GetProperty("titleGuess").GetString().ShouldBe("Zzxq Unreadable Spine Qqz");
        kept.GetProperty("isGuess").GetBoolean().ShouldBeTrue();
        kept.GetProperty("status").GetString().ShouldBe("unverified");
        kept.GetProperty("mediaHint").GetString().ShouldBe("books");
        kept.GetProperty("locationId").GetGuid().ShouldBe(locationId);
        kept.GetProperty("candidateId").GetGuid().ShouldBe(candidateId);
        kept.GetProperty("cropUrl").GetString().ShouldBe($"/media/covers/{cropId}");
        kept.GetProperty("resolvedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        // The T2 row leaves Review: it is rejected (skipped) with the link to its provisional.
        var row = body.GetProperty("candidate");
        Id(row).ShouldBe(candidateId);
        Decision(row).ShouldBe("rejected");
        var stored = (await ProvisionalAsync(provisionalId)).ShouldNotBeNull();
        stored.SessionId.ShouldBe(sessionId);
        stored.SnapId.ShouldBe((await CandidateAsync(candidateId)).SnapId);
        (await CandidateAsync(candidateId)).ProvisionalId.ShouldBe(provisionalId);
    }

    [Fact]
    public async Task Keeping_an_ambiguous_row_guesses_the_catalogs_top_alternative_not_the_raw_read()
    {
        // #435: the Suddenly You shape — garbled OCR text, the right title in the read's own guess, two catalog alternatives.
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Suddenly You", WorkKind.Book, 2001, format: ReleaseFormat.Paperback);
        await CatalogSeed.AddAsync(Db, "Suddenly You", WorkKind.Book, 2003, format: ReleaseFormat.Paperback);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        const string blurb = "The rare author who can make you laugh and cry—the same page. ẠồThe rare author who can make you laugh and cry-on the same pageại";
        var candidate = (await IdentifyOkAsync(
            editor, sessionId, Read(title: "Suddenly You", text: blurb, kind: "book", creator: "Lisa Kleypas")))[0];
        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("visibleText").GetString().ShouldBe(blurb);
        var top = candidate.GetProperty("alternatives")[0].GetProperty("title").GetString();

        var response = await KeepAsync(editor, sessionId, new { candidateId = Id(candidate) });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var kept = (await ReadJsonAsync(response)).GetProperty("provisional");
        kept.GetProperty("titleGuess").GetString().ShouldBe("Suddenly You");
        kept.GetProperty("titleGuess").GetString().ShouldBe(top);
    }

    [Fact]
    public async Task Keeping_without_a_crop_is_allowed_the_phone_may_have_lost_it()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);

        var response = await KeepAsync(editor, sessionId, new { candidateId });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var kept = (await ReadJsonAsync(response)).GetProperty("provisional");
        kept.GetProperty("cropUrl").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Keeping_the_same_row_twice_is_a_conflict_not_a_second_provisional()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        (await KeepAsync(editor, sessionId, new { candidateId })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await KeepAsync(editor, sessionId, new { candidateId });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProvisionalsOfAsync(library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Keeping_with_an_already_claimed_crop_is_a_conflict()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var first = Id((await IdentifyUnreadableAsync(editor, sessionId, "First kept Zzxq"))[0]);
        var second = Id((await IdentifyUnreadableAsync(editor, sessionId, "Second kept Zzxq"))[0]);
        var cropId = await UploadCropIdAsync(editor, sessionId, first);
        (await KeepAsync(editor, sessionId, new { candidateId = first, cropAssetId = cropId })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await KeepAsync(editor, sessionId, new { candidateId = second, cropAssetId = cropId });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProvisionalsOfAsync(library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Keeping_claims_only_an_unclaimed_crop_of_the_same_library()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        using var otherEditor = As(other, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var otherSessionId = await StartSessionAsync(otherEditor, other.Id);
        var otherCandidateId = Id((await IdentifyUnreadableAsync(otherEditor, otherSessionId))[0]);
        var foreignCrop = await UploadCropIdAsync(otherEditor, otherSessionId, otherCandidateId);

        var response = await KeepAsync(editor, sessionId, new { candidateId, cropAssetId = foreignCrop });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProvisionalsOfAsync(library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_session_lists_its_provisionals_and_counts_them_in_to_review()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        (await KeepAsync(editor, sessionId, new { candidateId })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var session = await GetSessionAsync(editor, sessionId);

        var provisionals = session.GetProperty("provisionals").EnumerateArray().ToList();
        provisionals.ShouldHaveSingleItem().GetProperty("candidateId").GetGuid().ShouldBe(candidateId);
        var counters = session.GetProperty("counters");
        counters.GetProperty("captured").GetInt32().ShouldBe(2); // the row plus its provisional
        counters.GetProperty("toReview").GetInt32().ShouldBe(1);
        counters.GetProperty("skipped").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Needs_attention_lists_open_provisionals_stalest_first()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var first = Id((await IdentifyUnreadableAsync(editor, sessionId, "First unreadable Zzxq"))[0]);
        var second = Id((await IdentifyUnreadableAsync(editor, sessionId, "Second unreadable Zzxq"))[0]);
        var firstId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId = first }));
        (await KeepAsync(editor, sessionId, new { candidateId = second })).StatusCode.ShouldBe(HttpStatusCode.Created);
        await DeleteProvisionalAsync(editor, library.Id, firstId);

        using var viewer = As(library, LibraryRole.Viewer);
        var response = await viewer.GetAsync(LibraryProvisionalsUrl(library.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var items = (await ReadJsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
        items.ShouldHaveSingleItem().GetProperty("titleGuess").GetString().ShouldBe("Second unreadable Zzxq");
    }

    [Fact]
    public async Task Promote_makes_an_item_from_the_guess_keeps_the_crop_as_its_cover_and_resolves()
    {
        var library = await CreateLibraryAsync();
        var locationId = await TestData.CreateLocationAsync(Db, library.Id, "Shelf A");
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { defaultLocationId = locationId });
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var cropId = await UploadCropIdAsync(editor, sessionId, candidateId);
        var provisionalId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId, cropAssetId = cropId }));

        var response = await PromoteAsync(editor, library.Id, provisionalId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        body.GetProperty("resolveAction").GetString().ShouldBe("promoted");
        body.GetProperty("resolvedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        var itemId = body.GetProperty("createdItemId").GetGuid();
        var stored = (await ProvisionalAsync(provisionalId)).ShouldNotBeNull();
        stored.ResolvedAt.ShouldNotBeNull();
        stored.ResolveAction.ShouldBe(ProvisionalResolveAction.Promoted);
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        var item = items.ShouldHaveSingleItem();
        item.Id.ShouldBe(itemId);
        item.LocationId.ShouldBe(locationId);
        item.CustomCoverAssetId.ShouldBe(cropId);
        (await AssetAsync(cropId)).ShouldNotBeNull(); // the crop lives on as the item's cover
        (await CandidateAsync(candidateId)).CreatedItemId.ShouldBe(itemId); // the session's counters reconcile
        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("counters").GetProperty("added").GetInt32().ShouldBe(1);
        session.GetProperty("counters").GetProperty("toReview").GetInt32().ShouldBe(0);
        await using (var context = Db.CreateContext())
        {
            (await context.MatchFeedback.IgnoreQueryFilters().CountAsync(f => f.CandidateId == candidateId)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Promote_is_idempotent_the_same_key_returns_the_same_item()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var provisionalId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId }));
        var key = Guid.NewGuid().ToString();

        var first = await PromoteAsync(editor, library.Id, provisionalId, key);
        var second = await PromoteAsync(editor, library.Id, provisionalId, key);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        (await ReadJsonAsync(first)).GetProperty("createdItemId").GetGuid()
            .ShouldBe((await ReadJsonAsync(second)).GetProperty("createdItemId").GetGuid());
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_to_existing_links_the_item_and_deletes_the_crop()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var cropId = await UploadCropIdAsync(editor, sessionId, candidateId);
        var provisionalId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId, cropAssetId = cropId }));
        var (_, itemId) = await OwnAsync(library, "Already shelved", ScanData.Upc(9201));

        var response = await editor.PostAsync(
            $"{ProvisionalUrl(library.Id, provisionalId)}:resolve", Json(new { itemId }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonAsync(response);
        body.GetProperty("resolveAction").GetString().ShouldBe("resolved_to_existing");
        body.GetProperty("createdItemId").GetGuid().ShouldBe(itemId);
        (await AssetAsync(cropId)).ShouldBeNull();
        FilesOnDisk().ShouldBeEmpty();
        var row = await CandidateAsync(candidateId);
        row.AlreadyOwnedCount.ShouldBe(1); // the row now answers "you own this"
        row.Resolution.ShouldBe(CandidateResolution.Matched);
        await using (var context = Db.CreateContext())
        {
            (await context.MatchFeedback.IgnoreQueryFilters().CountAsync(f => f.CandidateId == candidateId)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Resolve_to_an_item_of_another_library_is_not_found()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var provisionalId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId }));
        var (_, foreignItemId) = await OwnAsync(other, "Someone else's", ScanData.Upc(9202));

        var response = await editor.PostAsync(
            $"{ProvisionalUrl(library.Id, provisionalId)}:resolve", Json(new { itemId = foreignItemId }));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProvisionalAsync(provisionalId)).ShouldNotBeNull().ResolvedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Replacing_a_cited_use_my_photo_keeps_the_provisional_and_drops_only_the_snap_link()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        (await UploadPhotoAsync(editor, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var provisionalId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId }));
        (await ProvisionalAsync(provisionalId)).ShouldNotBeNull().SnapId.ShouldNotBeNull();

        var response = await UploadPhotoAsync(editor, sessionId, candidateId, JpegFixtures.Plain());

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var kept = (await ProvisionalAsync(provisionalId)).ShouldNotBeNull();
        kept.ResolvedAt.ShouldBeNull();
        kept.SnapId.ShouldBeNull();
    }

    [Fact]
    public async Task Committing_discards_a_cited_use_my_photo_but_keeps_the_provisional()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9401);
        await CatalogAsync("Commit beside a keep", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id); // commit mechanics, not auto-add (#341)
        var keptId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        (await UploadPhotoAsync(editor, sessionId, keptId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var provisionalId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId = keptId }));
        var addedId = Id((await ScanAsync(editor, sessionId, code))[0]);
        (await DecideAsync(editor, sessionId, addedId, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var kept = (await ProvisionalAsync(provisionalId)).ShouldNotBeNull();
        kept.ResolvedAt.ShouldBeNull();
        kept.SnapId.ShouldBeNull();
        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("counters").GetProperty("added").GetInt32().ShouldBe(1);
        session.GetProperty("counters").GetProperty("toReview").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Delete_removes_the_record_and_its_crop_and_reconciles_the_counters()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var cropId = await UploadCropIdAsync(editor, sessionId, candidateId);
        var provisionalId = await ProvisionalIdOf(await KeepAsync(editor, sessionId, new { candidateId, cropAssetId = cropId }));

        var response = await DeleteProvisionalAsync(editor, library.Id, provisionalId);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        (await ProvisionalAsync(provisionalId)).ShouldBeNull();
        (await AssetAsync(cropId)).ShouldBeNull();
        FilesOnDisk().ShouldBeEmpty();
        var counters = (await GetSessionAsync(editor, sessionId)).GetProperty("counters");
        counters.GetProperty("captured").GetInt32().ShouldBe(1);
        counters.GetProperty("toReview").GetInt32().ShouldBe(0);
        counters.GetProperty("skipped").GetInt32().ShouldBe(1);
    }

    private static async Task<Guid> UploadCropIdAsync(HttpClient client, Guid sessionId, Guid candidateId)
    {
        var response = await UploadCropAsync(client, sessionId, candidateId, JpegFixtures.Plain());
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("assetId").GetGuid();
    }

    private static async Task<Guid> ProvisionalIdOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("provisional").GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PromoteAsync(HttpClient client, Guid libraryId, Guid provisionalId, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ProvisionalUrl(libraryId, provisionalId)}:promote")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteProvisionalAsync(HttpClient client, Guid libraryId, Guid provisionalId) =>
        client.DeleteAsync(ProvisionalUrl(libraryId, provisionalId));
}
