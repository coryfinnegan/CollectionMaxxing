using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning;

/// <summary>
/// What a scan says about a release the library has on its wishlist, sold, or lost: only what you have is "owned" (owned, lent,
/// missing); a wishlisted release gets <c>wishlistItemId</c> and is never added beside it, and a sold one gets <c>soldItemId</c>.
/// </summary>
public class ScanWishlistAndSoldTests(ApiFixture api) : ScanTest(api)
{
    private static Guid? Fact(JsonElement candidate, string name) =>
        candidate.GetProperty(name).ValueKind == JsonValueKind.Null ? null : candidate.GetProperty(name).GetGuid();

    [Fact]
    public async Task A_missing_copy_counts_as_owned_on_a_scan()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9300);
        await OwnAsync(library, "Super Mario 64", code, status: ItemStatus.Missing);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        Decision(candidate).ShouldBe("rejected"); // an owned hit is answered on its card, not added
        Fact(candidate, "wishlistItemId").ShouldBeNull();
        Fact(candidate, "soldItemId").ShouldBeNull();
    }

    [Fact]
    public async Task A_wishlisted_release_is_not_owned_and_its_candidate_carries_the_wishlist_item()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9301);
        var (_, wishlistItemId) = await OwnAsync(library, "Super Mario 64", code, status: ItemStatus.Wishlist);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(0);
        Fact(candidate, "wishlistItemId").ShouldBe(wishlistItemId);
        Fact(candidate, "soldItemId").ShouldBeNull();
    }

    [Fact]
    public async Task A_wishlisted_hit_is_counted_as_skipped_not_owned_and_not_to_review()
    {
        var library = await CreateLibraryAsync();
        var wishlisted = ScanData.Upc(9307);
        var sold = ScanData.Upc(9308);
        await OwnAsync(library, "Super Mario 64", wishlisted, status: ItemStatus.Wishlist);
        await OwnAsync(library, "GoldenEye 007", sold, status: ItemStatus.Sold);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        await ScanAsync(editor, sessionId, wishlisted, sold);

        // The wishlisted one is answered on its card (Got it) and waits there, skipped like an owned hit; the sold one is an ordinary add.
        var counters = (await GetSessionAsync(editor, sessionId)).GetProperty("counters");
        counters.GetProperty("skipped").GetInt32().ShouldBe(1);
        counters.GetProperty("owned").GetInt32().ShouldBe(0);
        counters.GetProperty("toReview").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_scan_never_adds_an_owned_item_beside_the_wishlist_one_so_got_it_is_the_way_in()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9302);
        await OwnAsync(library, "Super Mario 64", code, status: ItemStatus.Wishlist);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        Decision(candidate).ShouldBe("rejected"); // the default is to leave it; the card offers Got it
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountItemsAsync(library.Id)).ShouldBe(1);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Single().Status.ShouldBe(ItemStatus.Wishlist);
    }

    [Fact]
    public async Task A_session_that_adds_on_its_own_still_leaves_a_wishlisted_release_to_the_person()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9303);
        await OwnAsync(library, "Super Mario 64", code, status: ItemStatus.Wishlist);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        candidate.GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await CountItemsAsync(library.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_sold_release_is_not_owned_and_is_added_as_usual_but_its_candidate_carries_the_sold_copy()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9304);
        var (releaseId, _) = await OwnAsync(library, "Super Mario 64", code, status: ItemStatus.Sold);
        await AddCopyAsync(library, releaseId, ItemStatus.Sold, addedMinutes: 10);
        var latest = await AddCopyAsync(library, releaseId, ItemStatus.Sold, addedMinutes: 20);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        candidate.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(0);
        Fact(candidate, "soldItemId").ShouldBe(latest);
        Fact(candidate, "wishlistItemId").ShouldBeNull();
        Decision(candidate).ShouldBe("accepted"); // buying it back is an ordinary add
    }

    [Fact]
    public async Task Another_librarys_wishlist_and_sold_copies_never_show_on_a_scan()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var code = ScanData.Upc(9305);
        var (releaseId, _) = await OwnAsync(theirs, "Super Mario 64", code, status: ItemStatus.Wishlist);
        await AddCopyAsync(theirs, releaseId, ItemStatus.Sold);
        using var editor = As(mine, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, mine.Id);

        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(0);
        Fact(candidate, "wishlistItemId").ShouldBeNull();
        Fact(candidate, "soldItemId").ShouldBeNull();
        Decision(candidate).ShouldBe("accepted");
    }

    [Fact]
    public async Task A_release_that_is_both_owned_and_wishlisted_answers_as_owned()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9306);
        var (releaseId, _) = await OwnAsync(library, "Super Mario 64", code);
        var wished = await AddCopyAsync(library, releaseId, ItemStatus.Wishlist);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        candidate.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        Fact(candidate, "wishlistItemId").ShouldBe(wished);
        Decision(candidate).ShouldBe("rejected");
    }
}
