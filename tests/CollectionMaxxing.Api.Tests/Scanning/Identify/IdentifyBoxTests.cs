using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// Where a read sits in the picture is kept on its row (#835), so a later step (the server's shelf read) and Review can point at it.
public class IdentifyBoxTests(ApiFixture api) : IdentifyTest(api)
{
    private static Dictionary<string, object?> WithBox(Dictionary<string, object?> read, double x)
    {
        read["box"] = new { x, y = 0.1, width = 0.05, height = 0.8 };
        return read;
    }

    private async Task<List<ScanCandidate>> StoredAsync(Guid sessionId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().Where(c => c.SessionId == sessionId).OrderBy(c => c.Seq).ToListAsync();
    }

    [Fact]
    public async Task A_phone_reads_box_is_stored_and_comes_back_on_the_candidate()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var response = await IdentifyAsync(editor, sessionId, null, WithBox(Read(title: "Star Fox 64", kind: "game"), 0.25));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        var row = (await StoredAsync(sessionId)).Single();
        row.BoxX.ShouldBe(0.25);
        row.BoxY.ShouldBe(0.1);
        row.BoxW.ShouldBe(0.05);
        row.BoxH.ShouldBe(0.8);
        row.BoxSource.ShouldBe(BoxSource.Ocr);
        var box = candidate.GetProperty("box");
        box.GetProperty("x").GetDouble().ShouldBe(0.25);
        box.GetProperty("h").GetDouble().ShouldBe(0.8);
        candidate.GetProperty("boxSource").GetString().ShouldBe("ocr");
    }

    [Fact]
    public async Task A_read_with_no_box_stores_none()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var response = await IdentifyAsync(editor, sessionId, null, Read(title: "Star Fox 64", kind: "game"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        var row = (await StoredAsync(sessionId)).Single();
        (row.BoxX, row.BoxY, row.BoxW, row.BoxH, row.BoxSource).ShouldBe((null, null, null, null, null));
        candidate.GetProperty("box").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_repeat_identify_keeps_the_box()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var snapId = Guid.NewGuid();
        var read = WithBox(Read(title: "Star Fox 64", kind: "game"), 0.25);
        (await IdentifyAsync(editor, sessionId, snapId, read)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = (await StoredAsync(sessionId)).Single();

        var again = await IdentifyAsync(editor, sessionId, snapId, Read(title: "Star Fox 64", kind: "game"));

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = (await StoredAsync(sessionId)).Single();
        after.Id.ShouldBe(before.Id);
        (after.BoxX, after.BoxY, after.BoxW, after.BoxH, after.BoxSource).ShouldBe((before.BoxX, before.BoxY, before.BoxW, before.BoxH, before.BoxSource));
        after.BoxX.ShouldBe(0.25);
    }

    [Fact]
    public async Task A_box_outside_the_picture_is_stored_as_none()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var read = Read(title: "Star Fox 64", kind: "game");
        read["box"] = new { x = 0.9, y = 0.1, width = 0.5, height = 0.8 };

        (await IdentifyAsync(editor, sessionId, null, read)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await StoredAsync(sessionId)).Single().BoxX.ShouldBeNull();
    }

    [Fact]
    public async Task A_bound_covers_box_is_stored()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await IdentifyOkAsync(editor, sessionId, Read(title: "A Game That Is Nowhere In The Catalogue")))[0];
        var bound = Body(Guid.NewGuid(), [WithBox(Read(title: "Star Fox 64", kind: "game"), 0.4)]);
        bound["kind"] = "cover";
        bound["shotKind"] = "single_cover";
        bound["candidateId"] = row.GetProperty("id").GetGuid();

        (await editor.PostAsync(IdentifyUrl(sessionId), Json(bound))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = (await StoredAsync(sessionId)).Single();
        (stored.BoxX, stored.BoxY, stored.BoxW, stored.BoxH, stored.BoxSource).ShouldBe((0.4, 0.1, 0.05, 0.8, BoxSource.Ocr));
    }

    [Fact]
    public async Task An_edition_proof_reads_box_is_stored()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var itemId = await TestData.CreateItemAsync(Db, library, releaseId);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var body = Body(Guid.NewGuid(), [WithBox(Read(title: "Dune", kind: "book", format: "paperback", year: 1965), 0.4)]);
        body["kind"] = "edition_proof";
        body["shotKind"] = "single_cover";
        body["itemId"] = itemId;

        (await editor.PostAsync(IdentifyUrl(sessionId), Json(body))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = (await StoredAsync(sessionId)).Single();
        (stored.BoxX, stored.BoxY, stored.BoxW, stored.BoxH, stored.BoxSource).ShouldBe((0.4, 0.1, 0.05, 0.8, BoxSource.Ocr));
    }
}
