using System.Net;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning;

// SCAN-1d (issue #105), contract item 3: Review lets the person correct a candidate's format, platform and edition. The
// correction is stored on the candidate, shown in its DTO, used when the item is added and when the candidate is searched again.
public class ScanCandidateEditTests(ApiFixture api) : ScanTest(api)
{
    private async Task<(TestLibrary Library, HttpClient Editor, Guid SessionId, System.Text.Json.JsonElement Candidate, Guid ReleaseId)> ScannedAsync(int seed)
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(seed);
        var releaseId = await CatalogAsync($"Edit game {seed}", code);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, code))[0];
        Resolution(candidate).ShouldBe("matched");
        return (library, editor, sessionId, candidate, releaseId);
    }

    [Fact]
    public async Task Patching_platform_and_edition_shows_in_the_candidate_and_in_the_created_item()
    {
        var (library, editor, sessionId, candidate, original) = await ScannedAsync(7501);
        var n64 = await ItemSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");

        var patched = await DecideAsync(editor, sessionId, Id(candidate), new { platform = "n64", edition = "Player's Choice", format = "game_cartridge" });

        patched.StatusCode.ShouldBe(HttpStatusCode.OK, await patched.Content.ReadAsStringAsync());
        var dto = await ReadJsonAsync(patched);
        var edits = dto.GetProperty("edits");
        edits.GetProperty("platform").GetString().ShouldBe("n64");
        edits.GetProperty("edition").GetString().ShouldBe("Player's Choice");
        edits.GetProperty("format").GetString().ShouldBe("game_cartridge");
        dto.GetProperty("userEdited").GetBoolean().ShouldBeTrue();
        Decision(dto).ShouldBe("accepted"); // an edit decides nothing

        var commit = await CommitAsync(editor, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = (await ScanReaders.ItemsAsync(Db, library.Id)).Single();
        await using var context = Db.CreateContext();
        var release = await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == item.ReleaseId);
        release.PlatformId.ShouldBe(n64);
        release.Edition.ShouldBe("Player's Choice");
        release.Format.ShouldBe(ReleaseFormat.GameCartridge);
        release.OwnerLibraryId.ShouldBe(library.Id, "no release had those, so the library got one of its own");
        release.WorkId.ShouldBe((await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == original)).WorkId, "same work");
    }

    [Fact]
    public async Task A_correction_that_matches_a_release_the_catalogue_already_has_uses_that_release()
    {
        var (library, editor, sessionId, candidate, original) = await ScannedAsync(7511);
        var n64 = await ItemSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        await using (var context = Db.CreateContext())
        {
            var work = (await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == original)).WorkId;
            var sibling = await ItemSeed.AddReleaseAsync(Db, "unused", WorkKind.Game, ReleaseFormat.GameCartridge, 1996, n64, workId: work);
            (await DecideAsync(editor, sessionId, Id(candidate), new { platform = "Nintendo 64" })).StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

            (await ScanReaders.ItemsAsync(Db, library.Id)).Single().ReleaseId.ShouldBe(sibling);
            (await context.Releases.IgnoreQueryFilters().CountAsync(r => r.WorkId == work)).ShouldBe(2, "nothing new was made");
        }
    }

    [Fact]
    public async Task Editing_a_candidate_without_changing_what_it_matched_adds_the_matched_release()
    {
        var (library, editor, sessionId, candidate, original) = await ScannedAsync(7521);

        (await DecideAsync(editor, sessionId, Id(candidate), new { format = "game_cartridge" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ScanReaders.ItemsAsync(Db, library.Id)).Single().ReleaseId.ShouldBe(original);
    }

    [Fact]
    public async Task A_blank_edition_clears_the_correction()
    {
        var (_, editor, sessionId, candidate, _) = await ScannedAsync(7531);
        await DecideAsync(editor, sessionId, Id(candidate), new { edition = "Greatest Hits" });

        var cleared = await ReadJsonAsync(await DecideAsync(editor, sessionId, Id(candidate), new { edition = "  " }));

        cleared.GetProperty("edits").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Over_long_platform_or_edition_text_and_a_made_up_format_are_rejected()
    {
        var (_, editor, sessionId, candidate, _) = await ScannedAsync(7541);
        var tooLong = new string('x', 101);

        var platform = await DecideAsync(editor, sessionId, Id(candidate), new { platform = tooLong });
        var edition = await DecideAsync(editor, sessionId, Id(candidate), new { edition = tooLong });
        var format = await DecideAsync(editor, sessionId, Id(candidate), new { format = "laser_pigeon" });

        platform.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        edition.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        format.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var current = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        current.GetProperty("edits").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        current.GetProperty("userEdited").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_search_again_scores_with_the_corrected_platform()
    {
        var library = await CreateLibraryAsync();
        var n64 = await ItemSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        var wii = await ItemSeed.AddPlatformAsync(Db, "Wii", "Wii");
        var (_, onN64) = await CatalogSeed.AddAsync(Db, "Mario Party", WorkKind.Game, 1999, n64, format: ReleaseFormat.GameCartridge);
        var (_, onWii) = await CatalogSeed.AddAsync(Db, "Mario Party", WorkKind.Game, 1999, wii, format: ReleaseFormat.GameDisc);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, ScanData.Upc(7551)))[0]; // a code nobody knows: something to fix

        async Task<System.Text.Json.JsonElement> SearchAsync()
        {
            var response = await editor.PostAsync($"{CandidateUrl(sessionId, Id(candidate))}:search", Json(new { text = "Mario Party" }));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return await ReadJsonAsync(response);
        }

        await DecideAsync(editor, sessionId, Id(candidate), new { platform = "Nintendo 64" });
        var withPlatform = await SearchAsync();

        var best = withPlatform.TryGetProperty("release", out var matched) && matched.ValueKind == System.Text.Json.JsonValueKind.Object
            ? matched.GetProperty("id").GetGuid()
            : withPlatform.GetProperty("alternatives")[0].GetProperty("releaseId").GetGuid();
        best.ShouldBe(onN64, "the corrected platform ranks its release first");
        best.ShouldNotBe(onWii);
    }

    [Fact]
    public async Task Editing_format_platform_and_edition_is_refused_once_the_session_is_abandoned()
    {
        var (_, editor, sessionId, candidate, _) = await ScannedAsync(7561);
        await ItemSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        (await editor.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var patched = await DecideAsync(editor, sessionId, Id(candidate), new { platform = "n64", edition = "Player's Choice", format = "game_cartridge" });

        patched.StatusCode.ShouldBe(HttpStatusCode.Conflict, await patched.Content.ReadAsStringAsync());
        var after = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        after.GetProperty("userEdited").GetBoolean().ShouldBeFalse();
        after.GetProperty("edits").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Editing_format_platform_and_edition_is_refused_once_the_session_is_committed()
    {
        var (library, editor, sessionId, candidate, original) = await ScannedAsync(7571);
        await ItemSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var patched = await DecideAsync(editor, sessionId, Id(candidate), new { platform = "n64", edition = "Player's Choice", format = "game_cartridge" });

        patched.StatusCode.ShouldBe(HttpStatusCode.Conflict, await patched.Content.ReadAsStringAsync());
        var after = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        after.GetProperty("userEdited").GetBoolean().ShouldBeFalse();
        after.GetProperty("edits").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Single().ReleaseId.ShouldBe(original, "the item keeps the release it was added with");
    }
}
