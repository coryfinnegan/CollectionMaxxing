using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning;

/// <summary>The scan session beyond the locked rules: how codes resolve, what the person can change, and what commit applies.</summary>
public class ScanSessionFlowTests(ApiFixture api) : ScanTest(api)
{
    [Fact]
    public async Task A_book_barcode_is_matched_at_a_book_provider_with_the_provider_score()
    {
        var library = await CreateLibraryAsync();
        Books.LookupResult = new Application.Catalog.Providers.ProviderRelease
        {
            Provider = "openlibrary",
            ExternalId = "OL1",
            Title = "Dune",
            Kind = Domain.Catalog.WorkKind.Book,
            Format = Domain.Catalog.ReleaseFormat.Paperback,
            Year = 1990,
            Creators = ["Frank Herbert"],
            Identifiers = Domain.Identifiers.Identifier.TryParse(ScanData.Isbn, out var isbn) ? [isbn!] : [],
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, ScanData.Isbn);

        Resolution(candidates[0]).ShouldBe("matched");
        candidates[0].GetProperty("score").GetDouble().ShouldBe(0.98);
        candidates[0].GetProperty("release").GetProperty("title").GetString().ShouldBe("Dune");
        candidates[0].GetProperty("rawCode").GetString().ShouldBe(ScanData.Isbn);
        Decision(candidates[0]).ShouldBe("accepted");

        // Scanning it again in a later session finds the release the first scan saved, without asking the provider.
        Books.LookupResult = null;
        var second = await StartSessionAsync(editor, library.Id);
        Resolution((await ScanAsync(editor, second, ScanData.Isbn))[0]).ShouldBe("matched");
    }

    [Fact]
    public async Task A_product_barcode_is_matched_through_the_name_search_and_traced_without_the_code()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9001);
        await Catalog.CatalogSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        Products.Know(code, ScanData.Product("Super Mario 64", code));
        Games.Results.Add(ScanData.Game("Super Mario 64"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);

        Resolution(candidates[0]).ShouldBe("matched");
        candidates[0].GetProperty("release").GetProperty("platform").GetProperty("name").GetString().ShouldBe("Nintendo 64");
        await using var context = Db.CreateContext();
        var trace = (await context.ScanCandidates.IgnoreQueryFilters().Select(c => c.TraceJson).SingleAsync())!;
        trace.ShouldContain("product-lookup");
        trace.ShouldContain("igdb");
        trace.ShouldNotContain(ScanData.Gtin13(code));
    }

    [Fact]
    public async Task A_product_nobody_can_match_is_not_found_but_keeps_its_name_for_fix_it()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9002);
        Products.Know(code, ScanData.Product("Obscure Game", code));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);

        Resolution(candidates[0]).ShouldBe("not_found");
        candidates[0].GetProperty("visibleText").GetString().ShouldBe("Obscure Game");
    }

    [Fact]
    public async Task Get_with_since_returns_only_later_candidates()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        await ScanAsync(viewer, sessionId, ScanData.Upc(9010), ScanData.Upc(9011));

        // since is the session's change counter (a candidate's changeSeq), not its position: SCAN-1d.
        var seenUpTo = (await GetSessionAsync(viewer, sessionId)).GetProperty("candidates").EnumerateArray()
            .Max(c => c.GetProperty("changeSeq").GetInt32());
        await ScanAsync(viewer, sessionId, ScanData.Upc(9012));

        var all = await GetSessionAsync(viewer, sessionId);
        var later = await GetSessionAsync(viewer, sessionId, since: seenUpTo);

        all.GetProperty("candidates").GetArrayLength().ShouldBe(3);
        later.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("seq").GetInt32()).ShouldBe([3]);
        (await viewer.GetAsync(SessionUrl(sessionId) + "?since=-1")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_session_defaults_apply_to_the_items_commit_creates()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf A");
        var code = ScanData.Upc(9020);
        await CatalogAsync("F-Zero X", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { defaultLocationId = shelf, defaultStatus = "wishlist" });

        await ScanAsync(editor, sessionId, code);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var item = (await ScanReaders.ItemsAsync(Db, library.Id)).Single();
        item.LocationId.ShouldBe(shelf);
        item.Status.ShouldBe(ItemStatus.Wishlist);
        item.AddedVia.ShouldBe(AddedVia.Barcode);
        item.CreatedBy.ShouldBe(library.EditorId);
    }

    [Fact]
    public async Task A_default_location_from_another_library_is_refused()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var foreign = await TestData.CreateLocationAsync(Db, other.Id, "Elsewhere");
        using var editor = As(library, LibraryRole.Editor);

        (await editor.PostAsync(SessionsUrl(library.Id), Json(new { defaultLocationId = foreign }))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rejecting_and_changing_copies_are_remembered_and_commit_skips_the_rejected()
    {
        var library = await CreateLibraryAsync();
        var first = ScanData.Upc(9030);
        var second = ScanData.Upc(9031);
        await CatalogAsync("Game A", first);
        await CatalogAsync("Game B", second);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, first, second);

        (await DecideAsync(editor, sessionId, Id(candidates[0]), new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var changed = await DecideAsync(editor, sessionId, Id(candidates[1]), new { copies = 3 });
        (await ReadJsonAsync(changed)).GetProperty("copies").GetInt32().ShouldBe(3);

        var result = await ReadJsonAsync(await CommitAsync(editor, sessionId));
        result.GetProperty("created").GetInt32().ShouldBe(3);
        result.GetProperty("skipped").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Deciding_needs_something_to_change_and_a_real_option()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9040);
        await CatalogAsync("Game C", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, code);
        var id = Id(candidates[0]);

        (await DecideAsync(editor, sessionId, id, new { })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DecideAsync(editor, sessionId, id, new { copies = 0 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DecideAsync(editor, sessionId, id, new { releaseId = Guid.NewGuid() })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_not_found_item_cannot_be_accepted_until_the_person_picks_a_release_by_searching()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(9050));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, ScanData.Upc(9051));
        var id = Id(candidates[0]);
        Resolution(candidates[0]).ShouldBe("not_found");

        (await DecideAsync(editor, sessionId, id, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var searched = await editor.PostAsync($"{CandidateUrl(sessionId, id)}:search", Json(new { text = "Star Fox 64" }));
        searched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(searched);
        Resolution(body).ShouldBe("ambiguous"); // a typed title is one signal: the person picks
        Decision(body).ShouldBe("undecided");
        body.GetProperty("visibleText").GetString().ShouldBe("Star Fox 64");
        var pick = body.GetProperty("alternatives")[0].GetProperty("releaseId").GetGuid();

        (await DecideAsync(editor, sessionId, id, new { decision = "accepted", releaseId = pick })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(await CommitAsync(editor, sessionId))).GetProperty("created").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Searching_with_no_text_is_a_validation_problem()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, ScanData.Upc(9060));

        var response = await editor.PostAsync($"{CandidateUrl(sessionId, Id(candidates[0]))}:search", Json(new { text = "  " }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Abandoning_ends_the_session_and_nothing_more_can_be_scanned_or_committed()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9070);
        await CatalogAsync("Game D", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, code);

        (await editor.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await editor.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent); // twice is fine

        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("abandoned");
        (await SubmitAsync(editor, sessionId, "late", Code(code))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CountItemsAsync(library.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_committed_session_cannot_be_abandoned()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var code = ScanData.Upc(9091);
        await CatalogAsync("Kirby 64", code);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, code);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await editor.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_code_that_ran_out_of_time_is_resolved_later_by_the_background_job()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9080);
        Products.Know(code, ScanData.Product("Mario Tennis", code));
        Games.Results.Add(ScanData.Game("Mario Tennis"));
        var slow = true;
        Products.Behaviour = async (id, ct) =>
        {
            if (slow)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }

            return Application.Scanning.ProductLookupResult.Found(ScanData.Product("Mario Tennis", code));
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);
        Resolution(candidates[0]).ShouldBe("pending");

        slow = false;
        using var scope = Factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<JobRunner>().RunNextAsync("test-worker", CancellationToken.None)).ShouldBeTrue();

        var resolved = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(resolved).ShouldBe("matched");
        Decision(resolved).ShouldBe("accepted");
    }

    [Fact]
    public async Task A_code_the_job_still_cannot_resolve_becomes_an_error_instead_of_waiting_forever()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        Products.Behaviour = (_, _) => Task.FromResult(Application.Scanning.ProductLookupResult.Unavailable);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        Resolution((await ScanAsync(editor, sessionId, ScanData.Upc(9081)))[0]).ShouldBe("pending");

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<JobRunner>().RunNextAsync("test-worker", CancellationToken.None);

        Resolution((await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0]).ShouldBe("error");
    }

    [Fact]
    public async Task A_reused_barcode_with_two_releases_asks_the_person_to_pick()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9090);
        await CatalogAsync("Reused A", code);
        await CatalogAsync("Reused B", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);

        Resolution(candidates[0]).ShouldBe("ambiguous");
        Decision(candidates[0]).ShouldBe("undecided");
        candidates[0].GetProperty("alternatives").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A_private_release_of_another_library_is_never_a_local_match()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var code = ScanData.Upc(9091);
        Domain.Identifiers.Identifier.TryParse(code, out var id).ShouldBeTrue();
        await Catalog.CatalogSeed.AddAsync(Db, "Secret Game", Domain.Catalog.WorkKind.Game, 2000, ownerLibraryId: other.Id, barcode: id!.Value);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);

        Resolution(candidates[0]).ShouldBe("not_found");
        candidates[0].GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_candidate_is_stored_with_the_session_and_library()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        await ScanAsync(viewer, sessionId, ScanData.Upc(9092));

        await using var context = Db.CreateContext();
        var candidate = await context.ScanCandidates.IgnoreQueryFilters().SingleAsync();
        candidate.LibraryId.ShouldBe(library.Id);
        candidate.SessionId.ShouldBe(sessionId);
        candidate.Source.ShouldBe(CandidateSource.Barcode);
        candidate.Seq.ShouldBe(1);
    }
}
