using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// The server's read settles the snap server first (#814): its finds stand, and the phone's rows the rule leaves out (a Vision read it does not keep,
// a read only the phone's model made) become server skips with the reason server_did_not_see, which the person can put back. A row the person acted
// on is never changed, and a release a phone row already has is never added twice. A fake reader; real Postgres; made-up game titles.
public class ShelfReadServerFirstTests(ApiFixture api) : ShelfReadTest(api)
{
    private const string Harbor = "Quill Harbor Tides";
    private const string Meadow = "Copper Meadow Song";
    private const string Orchard = "Velvet Orchard Road";
    private const string Lantern = "Ashen Lantern Row";

    private static object OptIn() => new { autoCommitTiers = new[] { "T0", "T1" } };

    private static Dictionary<string, object?> PhoneRead(string title, bool modelOnly = false, string kind = "game")
    {
        var read = Read(title: title, kind: kind, format: "game_cartridge", year: 1996);
        if (modelOnly)
        {
            read["unconfirmed"] = true;
        }

        return read;
    }

    private async Task<(TestLibrary Library, HttpClient Editor, Guid SessionId)> ArrangeAsync(object? session, params string[] catalogue)
    {
        var library = await CreateLibraryAsync();
        for (var i = 0; i < catalogue.Length; i++)
        {
            await CatalogAsync(catalogue[i], ScanData.Upc(9700 + i));
        }

        var editor = As(library, LibraryRole.Editor);
        var sessionId = session is null ? await StartSessionWithoutAutoCommitAsync(editor, library.Id) : await StartSessionAsync(editor, library.Id, session);
        return (library, editor, sessionId);
    }

    private async Task<List<ScanCandidate>> RowsAsync(Guid sessionId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().Where(c => c.SessionId == sessionId).OrderBy(c => c.Seq).ToListAsync();
    }

    private static string? SkipReason(JsonElement candidate) =>
        candidate.TryGetProperty("skipReason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;

    private static string ReadAs(ScanCandidate row) => row.VisibleText!;

    private static async Task<Dictionary<string, JsonElement>> ShownAsync(HttpClient client, Guid sessionId) =>
        (await GetSessionAsync(client, sessionId)).GetProperty("candidates").EnumerateArray()
            .ToDictionary(c => c.GetProperty("visibleText").GetString()!);

    [Fact]
    public async Task A_vision_read_of_one_word_the_server_did_not_see_becomes_a_server_skip_that_can_be_put_back()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, Harbor, Meadow);
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead(Harbor), PhoneRead("Copper"));
        var before = (await RowsAsync(sessionId)).Single(r => ReadAs(r) == "Copper");
        before.Resolution.ShouldNotBe(CandidateResolution.Pending);
        Reader.Answer = _ => ScriptedReader.Found(Harbor);

        var answered = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        answered.Select(Id).ShouldBe([before.Id], "the server added nothing (its find is the phone's row) and skipped the fragment");
        Decision(answered[0]).ShouldBe("rejected");
        SkipReason(answered[0]).ShouldBe("server_did_not_see");
        var shown = await ShownAsync(editor, sessionId);
        SkipReason(shown["Copper"]).ShouldBe("server_did_not_see");
        SkipReason(shown[Harbor]).ShouldBeNull();
        Decision(shown[Harbor]).ShouldNotBe("rejected", "the row the server's find agrees with stands");

        var putBack = await ReadJsonAsync(await DecideAsync(editor, sessionId, before.Id, new { decision = "undecided" }));

        Decision(putBack).ShouldBe("undecided");
        SkipReason(putBack).ShouldBeNull();
    }

    [Fact]
    public async Task A_read_only_the_phones_model_made_becomes_a_server_skip_and_a_vision_read_the_rule_keeps_is_left_as_it_was()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, Harbor, Meadow, Orchard);
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead(Meadow), PhoneRead(Orchard, modelOnly: true));
        var before = await RowsAsync(sessionId);
        Reader.Answer = _ => ScriptedReader.Found(Harbor);

        var answered = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        answered.Select(c => (c.GetProperty("visibleText").GetString(), SkipReason(c))).ShouldBe(
            [(Harbor, null), (Orchard, "server_did_not_see")], "the server's find, then the model-only row it left out");
        var after = await RowsAsync(sessionId);
        var kept = before.Single(r => ReadAs(r) == Meadow);
        after.Single(r => r.Id == kept.Id).Version.ShouldBe(kept.Version, "a Vision read of another title with two words stands untouched");
    }

    [Fact]
    public async Task A_vision_read_of_a_kind_the_server_read_nothing_of_becomes_a_server_skip()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync(Harbor, ScanData.Upc(9721));
        await CatalogAsync(Lantern, ScanData.Upc(9722), ReleaseFormat.Paperback, WorkKind.Book);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead(Harbor), Read(title: Lantern, kind: "book", year: 1996));
        Reader.Answer = _ => ScriptedReader.Found(Harbor); // a game: the server read no book on this shelf

        var answered = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        answered.Select(c => (c.GetProperty("visibleText").GetString(), SkipReason(c))).ShouldBe([(Lantern, "server_did_not_see")]);
    }

    [Fact]
    public async Task A_release_a_model_only_row_already_has_is_not_added_again_and_that_row_stands()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, Harbor, Meadow);
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead(Harbor), PhoneRead(Meadow, modelOnly: true));
        var before = await RowsAsync(sessionId);
        Reader.Answer = _ => ScriptedReader.Found(Harbor, Meadow);

        var answered = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        answered.ShouldBeEmpty("both finds are rows the phone already shows");
        var after = await RowsAsync(sessionId);
        after.Count.ShouldBe(2);
        foreach (var row in before)
        {
            after.Single(r => r.Id == row.Id).Version.ShouldBe(row.Version, $"{ReadAs(row)} was written");
        }
    }

    [Fact]
    public async Task The_vision_row_the_server_agrees_with_is_the_row_and_is_added_by_itself_once()
    {
        var (library, editor, sessionId) = await ArrangeAsync(OptIn(), Harbor);
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead(Harbor));
        Reader.Answer = _ => ScriptedReader.Found(Harbor);

        (await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap))).ShouldBeEmpty();

        var row = (await RowsAsync(sessionId)).Single();
        row.Source.ShouldBe(CandidateSource.PictureAppleModel);
        row.CreatedItemId.ShouldNotBeNull();
        (await CountItemsAsync(library.Id)).ShouldBe(1, "two engines agree: one item, never a second for the server's read");
    }

    [Fact]
    public async Task A_row_the_person_decided_or_edited_or_that_was_added_by_itself_is_never_skipped()
    {
        var (library, editor, sessionId) = await ArrangeAsync(OptIn(), Harbor, Meadow, Orchard);
        using var _1 = editor;
        await CatalogAsync(Lantern, ScanData.Upc(9731), ReleaseFormat.Paperback, WorkKind.Book);

        // Each is a read the rule leaves out (a book where the server reads only games, or one only the phone's model made).
        var snap = await IdentifyShelfSnapAsync(
            editor, sessionId, "shelf_segment", Read(title: Lantern, kind: "book", year: 1996), PhoneRead(Meadow, modelOnly: true), PhoneRead(Orchard, modelOnly: true));
        var rows = await RowsAsync(sessionId);
        var accepted = rows.Single(r => ReadAs(r) == Meadow);
        var edited = rows.Single(r => ReadAs(r) == Orchard);
        rows.Single(r => ReadAs(r) == Lantern).CreatedItemId.ShouldNotBeNull("a Vision read added by itself before the server read");
        (await DecideAsync(editor, sessionId, accepted.Id, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync(editor, sessionId, edited.Id, new { edition = "Player's Choice" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await RowsAsync(sessionId);
        var itemsBefore = await CountItemsAsync(library.Id);
        Reader.Answer = _ => ScriptedReader.Found("Silver Thicket Fair");

        var answered = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        var after = await RowsAsync(sessionId);
        foreach (var row in before.Where(r => r.Id == accepted.Id || r.Id == edited.Id || r.CreatedItemId is not null))
        {
            var same = after.Single(r => r.Id == row.Id);
            same.Version.ShouldBe(row.Version, $"{ReadAs(row)} was written");
            same.Decision.ShouldBe(row.Decision);
            same.CreatedItemId.ShouldBe(row.CreatedItemId);
        }

        answered.ShouldAllBe(c => SourceOf(c) == "picture_server_model" || SkipReason(c) == "server_did_not_see");
        answered.Select(Id).ShouldNotContain(accepted.Id);
        answered.Select(Id).ShouldNotContain(edited.Id);
        (await CountItemsAsync(library.Id)).ShouldBe(itemsBefore, "nothing the server read is added by itself, and no item was undone");
    }

    [Fact]
    public async Task Sending_the_snap_again_changes_nothing_more_and_answers_the_same_rows()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, Harbor, Meadow, Orchard);
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead(Harbor), PhoneRead(Orchard, modelOnly: true));
        Reader.Answer = _ => ScriptedReader.Found(Harbor, Meadow);

        var first = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));
        var between = await RowsAsync(sessionId);
        var second = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        first.Select(c => (c.GetProperty("visibleText").GetString(), SkipReason(c))).ShouldBe([(Meadow, null), (Orchard, "server_did_not_see")]);
        second.Select(Id).ShouldBe(first.Select(Id));
        Reader.Calls.ShouldBe(1);
        var after = await RowsAsync(sessionId);
        after.Select(r => (r.Id, r.Version)).ShouldBe(between.Select(r => (r.Id, r.Version)));
    }

    /// <summary>The game provider answers only once <paramref name="slow"/> says so; until then every ask runs out of time.</summary>
    private void SlowGames(Func<bool> slow, params string[] titles)
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        foreach (var title in titles)
        {
            Games.Results.Add(ScanData.Game(title, "Nintendo 64", 1998, "g-" + title.Replace(' ', '-')));
        }

        Games.Behaviour = async ct =>
        {
            if (slow())
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
    }

    [Fact]
    public async Task A_server_row_the_job_finishes_with_a_release_no_vision_row_names_stays_for_the_person_with_auto_add_on()
    {
        var slow = true;
        SlowGames(() => slow, "Zorblax Harbor Saga");
        var (library, editor, sessionId) = await ArrangeAsync(OptIn(), Meadow);
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead(Meadow));
        var itemsBefore = await CountItemsAsync(library.Id);
        Reader.Answer = _ => ScriptedReader.Found("Zorblax Harbor Saga");
        var added = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));
        Resolution(added.Single()).ShouldBe("pending");

        slow = false;
        await RunNextJobAsync();

        var row = (await RowsAsync(sessionId)).Single(r => r.Source == CandidateSource.PictureServerModel);
        row.Resolution.ShouldBe(CandidateResolution.Matched);
        row.Decision.ShouldBe(CandidateDecision.Undecided);
        row.CreatedItemId.ShouldBeNull();
        (await CountItemsAsync(library.Id)).ShouldBe(itemsBefore, "only the server read it: the person ticks it");
    }

    [Fact]
    public async Task A_server_row_the_job_finishes_with_the_release_a_vision_row_shows_is_a_repeat_and_the_item_is_added_once()
    {
        var slow = true;
        SlowGames(() => slow, "Zorblax Lantern Saga");
        var (library, editor, sessionId) = await ArrangeAsync(OptIn());
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Zorblax Lantern Saga"));
        Reader.Answer = _ => ScriptedReader.Found("Zorblax Lantern Saga");
        var added = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));
        Resolution(added.Single()).ShouldBe("pending", "neither engine's read has an answer yet, so the server's row is added");

        slow = false;
        await RunNextJobAsync(); // the phone's row: two engines read it, it is the row and is added by itself
        await RunNextJobAsync(); // the server's row

        var rows = await RowsAsync(sessionId);
        var phone = rows.Single(r => r.Source != CandidateSource.PictureServerModel);
        var server = rows.Single(r => r.Source == CandidateSource.PictureServerModel);
        phone.CreatedItemId.ShouldNotBeNull();
        server.Resolution.ShouldNotBe(CandidateResolution.Pending);
        server.Decision.ShouldBe(CandidateDecision.Rejected);
        server.CreatedItemId.ShouldBeNull();
        var shown = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates").EnumerateArray().Single(c => Id(c) == server.Id);
        SkipReason(shown).ShouldBe("repeat");
        (await CountItemsAsync(library.Id)).ShouldBe(1, "one item for the release, never a second for the server's read");
    }

    [Fact]
    public async Task A_read_that_could_not_be_made_changes_nothing()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, Harbor, Orchard);
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead(Harbor), PhoneRead(Orchard, modelOnly: true));
        var before = await RowsAsync(sessionId);
        Reader.Answer = _ => Application.ShelfReading.ShelfReadResult.Empty(Application.ShelfReading.ShelfReadStatus.TimedOut);

        (await ReadShelfAsync(editor, sessionId, snap)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        (await RowsAsync(sessionId)).Select(r => (r.Id, r.Version)).ShouldBe(before.Select(r => (r.Id, r.Version)));
    }
}
