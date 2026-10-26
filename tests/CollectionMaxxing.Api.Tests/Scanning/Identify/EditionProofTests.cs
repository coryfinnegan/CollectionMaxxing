using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// Issue #346 (design §11): identify accepts kind edition_proof bound to a library item (cover + copyright page for a
// book with no barcode). A match on the item's own work upgrades its release link and verifies it; anything else
// becomes a T2 row and the item is never touched.
public class EditionProofTests(ApiFixture api) : IdentifyTest(api)
{
    private static string? Tier(JsonElement candidate) =>
        candidate.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String ? tier.GetString() : null;

    private static Guid? CreatedItemId(JsonElement candidate) =>
        candidate.TryGetProperty("createdItemId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null;

    private static Dictionary<string, object?> Proof(Guid snapId, Guid itemId, object read)
    {
        var body = Body(snapId, [read]);
        body["kind"] = "edition_proof";
        body["shotKind"] = "single_cover";
        body["itemId"] = itemId;
        return body;
    }

    private async Task<Item> ReadItemAsync(Guid itemId)
    {
        await using var context = Db.CreateContext();
        return await context.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == itemId);
    }

    [Fact]
    public async Task An_edition_proof_match_upgrades_the_item_and_verifies_it()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var itemId = await TestData.CreateItemAsync(Db, library, releaseId);
        await ItemSeed.SetEditionUnverifiedAsync(Db, itemId);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var body = Proof(Guid.NewGuid(), itemId, Read(title: "Dune", kind: "book", format: "paperback", year: 1965));
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();
        Resolution(candidate).ShouldBe("matched");
        CreatedItemId(candidate).ShouldBe(itemId, "the row is the item now, not another thing to review");
        var stored = await ReadItemAsync(itemId);
        stored.ReleaseId.ShouldBe(releaseId);
        stored.EditionVerified.ShouldBeTrue();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1, "a proof verifies; it never adds");
    }

    [Fact]
    public async Task An_edition_proof_of_another_edition_swaps_the_release()
    {
        var library = await CreateLibraryAsync();
        var first = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var workId = await ItemSeed.WorkOfAsync(Db, first);
        var second = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Hardcover, 1965, workId: workId);
        var itemId = await TestData.CreateItemAsync(Db, library, first);
        await ItemSeed.SetEditionUnverifiedAsync(Db, itemId);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var body = Proof(Guid.NewGuid(), itemId, Read(title: "Dune", kind: "book", localMatchReleaseId: second));
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();
        CreatedItemId(candidate).ShouldBe(itemId);
        var stored = await ReadItemAsync(itemId);
        stored.ReleaseId.ShouldBe(second);
        stored.EditionVerified.ShouldBeTrue();
    }

    [Fact]
    public async Task An_edition_proof_of_another_work_becomes_a_T2_row_and_leaves_the_item_alone()
    {
        var library = await CreateLibraryAsync();
        var mine = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var other = await ItemSeed.AddReleaseAsync(Db, "Dune Messiah", WorkKind.Book, ReleaseFormat.Paperback, 1969);
        var itemId = await TestData.CreateItemAsync(Db, library, mine);
        await ItemSeed.SetEditionUnverifiedAsync(Db, itemId);
        var before = await ReadItemAsync(itemId);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0", "T1" } });

        var body = Proof(Guid.NewGuid(), itemId, Read(title: "Dune Messiah", kind: "book", localMatchReleaseId: other));
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();
        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T2", "a proof that disagrees is reviewed, never applied and never auto-added");
        Decision(candidate).ShouldBe("accepted", "a matched row lands pre-checked for review, like any matched T2 row");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
        var after = await ReadItemAsync(itemId);
        after.ReleaseId.ShouldBe(before.ReleaseId);
        after.EditionVerified.ShouldBeFalse();
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
    }

    [Fact]
    public async Task An_edition_proof_that_matches_nothing_becomes_a_T2_row()
    {
        var library = await CreateLibraryAsync();
        var mine = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var itemId = await TestData.CreateItemAsync(Db, library, mine);
        await ItemSeed.SetEditionUnverifiedAsync(Db, itemId);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var body = Proof(Guid.NewGuid(), itemId, Read(title: "A Book That Is Nowhere In The Catalogue"));
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();
        Resolution(candidate).ShouldBe("not_found");
        Tier(candidate).ShouldBe("T2");
        CreatedItemId(candidate).ShouldBeNull();
        (await ReadItemAsync(itemId)).EditionVerified.ShouldBeFalse();
    }

    [Theory]
    [InlineData("shelf", false, "scan.bound_proof_kind")]
    [InlineData("cover", false, "scan.bound_proof_kind")]
    [InlineData("edition_proof", true, "scan.proof_item_required")]
    public async Task An_edition_proof_needs_kind_edition_proof_and_an_item(string kind, bool omitItem, string code)
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var body = Body(Guid.NewGuid(), [Read(title: "Dune")]);
        body["kind"] = kind;
        if (!omitItem)
        {
            body["itemId"] = Guid.NewGuid();
        }

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task An_edition_proof_carries_exactly_one_item()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var body = Body(Guid.NewGuid(), [Read(title: "Dune"), Read(title: "Dune Messiah")]);
        body["kind"] = "edition_proof";
        body["itemId"] = Guid.NewGuid();

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.proof_single_item");
    }

    [Fact]
    public async Task A_read_bound_to_both_a_row_and_an_item_is_refused()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await IdentifyOkAsync(editor, sessionId, Read(title: "A Book That Is Nowhere In The Catalogue")))[0];
        var body = Body(Guid.NewGuid(), [Read(title: "Dune")]);
        body["kind"] = "edition_proof";
        body["candidateId"] = Id(row);
        body["itemId"] = Guid.NewGuid();

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.bound_one_row");
    }

    [Fact]
    public async Task An_edition_proof_for_an_unknown_item_is_404()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var body = Proof(Guid.NewGuid(), Guid.NewGuid(), Read(title: "Dune", kind: "book"));
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.not_found");
        (await CountCandidatesAsync(sessionId)).ShouldBe(0, "an unknown item leaves no row behind");
    }

    [Fact]
    public async Task An_edition_proof_for_another_librarys_item_is_404()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var foreign = await TestData.CreateItemAsync(Db, other, releaseId);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var body = Proof(Guid.NewGuid(), foreign, Read(title: "Dune", kind: "book"));
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CountCandidatesAsync(sessionId)).ShouldBe(0);
    }

    [Fact]
    public async Task Repeating_an_edition_proof_replays_without_a_second_row()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var itemId = await TestData.CreateItemAsync(Db, library, releaseId);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var body = Proof(Guid.NewGuid(), itemId, Read(title: "Dune", kind: "book", format: "paperback", year: 1965));

        var first = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var retry = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());

        var one = (await ReadJsonAsync(first)).GetProperty("candidates").EnumerateArray().Single();
        var two = (await ReadJsonAsync(retry)).GetProperty("candidates").EnumerateArray().Single();
        Id(two).ShouldBe(Id(one));
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_pending_proof_finishes_as_a_T2_row_through_its_job_without_adding()
    {
        var library = await CreateLibraryAsync();
        var mine = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var itemId = await TestData.CreateItemAsync(Db, library, mine);
        await ItemSeed.SetEditionUnverifiedAsync(Db, itemId);
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var slow = true;
        Games.Results.Add(ScanData.Game("Star Fox 64"));
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0", "T1" } });

        var body = Proof(Guid.NewGuid(), itemId, Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996));
        var pending = (await ReadJsonAsync(await editor.PostAsync(IdentifyUrl(sessionId), Json(body))))
            .GetProperty("candidates").EnumerateArray().Single();
        Resolution(pending).ShouldBe("pending");

        slow = false;
        await RunNextJobAsync();

        var session = await GetSessionAsync(editor, sessionId);
        var finished = session.GetProperty("candidates").EnumerateArray().Single(c => Id(c) == Id(pending));
        Resolution(finished).ShouldBe("matched");
        Tier(finished).ShouldBe("T2", "the job carries the proof binding: a mismatch is reviewed, never added");
        CreatedItemId(finished).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1, "only the original item stands");
        var stored = await ReadItemAsync(itemId);
        stored.ReleaseId.ShouldBe(mine);
        stored.EditionVerified.ShouldBeFalse();
    }
}
