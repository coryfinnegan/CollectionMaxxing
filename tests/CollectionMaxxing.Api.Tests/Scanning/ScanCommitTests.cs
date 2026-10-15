using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning;

// LOCKED acceptance tests of SCAN-1a (issue #61): the commit and duplicate rules that protect the library from wrong or double adds.
// Do not edit or weaken.
public class ScanCommitTests(ApiFixture api) : ScanTest(api)
{
    [Fact]
    public async Task Commit_is_atomic_when_one_item_cannot_be_created_none_are_created()
    {
        var library = await CreateLibraryAsync();
        var otherLibrary = await CreateLibraryAsync();
        var first = ScanData.Upc(3001);
        var second = ScanData.Upc(3002);
        await CatalogAsync("Pilotwings 64", first);
        var secondRelease = await CatalogAsync("Blast Corps", second);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, first, second);
        candidates.ShouldAllBe(c => Decision(c) == "accepted");

        // Between scanning and committing, the second release becomes another library's private release: this library may not use it.
        await using (var context = Db.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"update releases set owner_library_id = {otherLibrary.Id} where id = {secondRelease}");
        }

        var failed = await CommitAsync(editor, sessionId);

        failed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CountItemsAsync(library.Id)).ShouldBe(0); // not even the first one
        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("status").GetString().ShouldBe("open");
        session.GetProperty("candidates").EnumerateArray().ShouldAllBe(c => c.GetProperty("createdItemId").ValueKind == System.Text.Json.JsonValueKind.Null);

        // Once the problem is gone, the same session commits whole.
        await using (var context = Db.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"update releases set owner_library_id = null where id = {secondRelease}");
        }

        var retried = await CommitAsync(editor, sessionId);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(retried)).GetProperty("created").GetInt32().ShouldBe(2);
        (await CountItemsAsync(library.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task A_second_commit_of_the_same_session_creates_nothing()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(3010);
        await CatalogAsync("Banjo-Kazooie", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, code);

        var first = await CommitAsync(editor, sessionId, "commit-key-1");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await ReadJsonAsync(first);
        firstBody.GetProperty("created").GetInt32().ShouldBe(1);

        // A retry of the same request gets the same answer.
        var replay = await CommitAsync(editor, sessionId, "commit-key-1");
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(replay)).GetProperty("itemIds")[0].GetGuid().ShouldBe(firstBody.GetProperty("itemIds")[0].GetGuid());

        // A new request for a session that is already committed is refused.
        var again = await CommitAsync(editor, sessionId, "commit-key-2");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await CountItemsAsync(library.Id)).ShouldBe(1);
        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("committed");
    }

    [Fact]
    public async Task An_exactly_owned_release_is_skipped_by_default_and_never_added()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(3020);
        await OwnAsync(library, "GoldenEye 007", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);

        Resolution(candidates[0]).ShouldBe("matched");
        candidates[0].GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        Decision(candidates[0]).ShouldBe("rejected"); // Skip
        var commit = await CommitAsync(editor, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await ReadJsonAsync(commit);
        result.GetProperty("created").GetInt32().ShouldBe(0);
        result.GetProperty("skipped").GetInt32().ShouldBe(1);
        (await CountItemsAsync(library.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Add_another_copy_adds_an_owned_release_only_when_the_user_chose_it()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(3021);
        await OwnAsync(library, "Diddy Kong Racing", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, code);
        Decision(candidates[0]).ShouldBe("rejected");

        (await DecideAsync(editor, sessionId, Id(candidates[0]), new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var commit = await CommitAsync(editor, sessionId);

        (await ReadJsonAsync(commit)).GetProperty("created").GetInt32().ShouldBe(1);
        (await CountItemsAsync(library.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task A_different_format_copy_of_an_owned_work_is_added_by_default()
    {
        var library = await CreateLibraryAsync();
        var (vhsRelease, _) = await OwnAsync(
            library, "The Matrix", code: null, format: ReleaseFormat.Vhs, kind: WorkKind.Film, year: 1999);
        var dvdCode = ScanData.Upc(3030);
        await AddReleaseToWorkOfAsync(vhsRelease, ReleaseFormat.Dvd, dvdCode);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, dvdCode);

        Resolution(candidates[0]).ShouldBe("matched");
        candidates[0].GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(0); // not this release
        Decision(candidates[0]).ShouldBe("accepted"); // Add
        var commit = await CommitAsync(editor, sessionId);
        (await ReadJsonAsync(commit)).GetProperty("created").GetInt32().ShouldBe(1);
        (await CountItemsAsync(library.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task A_low_confidence_or_ambiguous_match_is_never_auto_accepted_and_lands_in_pick_one_with_the_top_3()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(3040);
        Products.Know(code, ScanData.Product("Madden NFL", code, platform: "PlayStation 2", year: 2003));
        Games.Results.AddRange(
        [
            ScanData.Game("Madden NFL 2004", "PlayStation 2", 2003, "m4"),
            ScanData.Game("Madden NFL 2005", "PlayStation 2", 2004, "m5"),
            ScanData.Game("Madden NFL 2006", "PlayStation 2", 2005, "m6"),
            ScanData.Game("Madden NFL 2007", "PlayStation 2", 2006, "m7"),
        ]);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);

        Resolution(candidates[0]).ShouldBe("ambiguous");
        Decision(candidates[0]).ShouldBe("undecided");
        candidates[0].GetProperty("release").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        var alternatives = candidates[0].GetProperty("alternatives");
        alternatives.GetArrayLength().ShouldBe(3);
        alternatives.EnumerateArray().ShouldAllBe(a => a.GetProperty("releaseId").GetGuid() != Guid.Empty);

        // Commit ignores what nobody decided.
        var commit = await CommitAsync(editor, sessionId);
        (await ReadJsonAsync(commit)).GetProperty("created").GetInt32().ShouldBe(0);
        (await CountItemsAsync(library.Id)).ShouldBe(0);

        // Picking one is the user's decision, and then it is added.
        var pick = alternatives[1].GetProperty("releaseId").GetGuid();
        var picked = await DecideAsync(editor, sessionId, Id(candidates[0]), new { decision = "accepted", releaseId = pick });
        picked.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(picked)).GetProperty("release").GetProperty("id").GetGuid().ShouldBe(pick);
    }

    [Fact]
    public async Task A_code_nobody_knows_is_not_found_and_commit_ignores_it()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, ScanData.Upc(3050));

        Resolution(candidates[0]).ShouldBe("not_found");
        Decision(candidates[0]).ShouldBe("undecided");
        (await ReadJsonAsync(await CommitAsync(editor, sessionId))).GetProperty("created").GetInt32().ShouldBe(0);
        (await CountItemsAsync(library.Id)).ShouldBe(0);
    }

    private async Task AddReleaseToWorkOfAsync(Guid releaseId, ReleaseFormat format, string code)
    {
        Domain.Identifiers.Identifier.TryParse(code, out var id).ShouldBeTrue();
        await using var context = Db.CreateContext();
        var workId = await context.Releases.IgnoreQueryFilters().Where(r => r.Id == releaseId).Select(r => r.WorkId).SingleAsync();
        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = workId,
            Format = format,
            Source = ReleaseSource.Provider,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.Releases.Add(release);
        context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = release.Id, Type = id!.Type, Value = id.Value });
        await context.SaveChangesAsync();
    }
}
