using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// The rule that settles a shelf once the server has read it (PcShelfUpgrade, #814) is the one the eval's server_vision row is built with, so the
// eval measures what the app shows. One shelf goes through both: the eval's EvalCombination.OfServerRead over grounded rows, and the :read-shelf
// endpoint over the rows identify stored for the same reads. Both show the same releases and leave out the same phone reads, the phone model's own
// reads among them: the endpoint skips those, and the eval row never had them (F5 of #812's review: both compare against the same rows now).
// Made-up titles; real Postgres; a fake reader.
public class PcShelfUpgradeParityTests(ApiFixture api) : ShelfReadTest(api)
{
    private const string Harbor = "Quill Harbor Tides"; // the server and Vision both read it
    private const string Delta = "Quiet Delta Run"; // only the server read it
    private const string Orchard = "Velvet Orchard Road"; // a Vision read the rule keeps
    private const string Meadow = "Copper Meadow Song"; // Vision read one word of it ("Copper"), and the phone's model read it whole
    private const string Lantern = "Ashen Lantern Row"; // a book, where the server read only games

    private static GroundedItem Game(string title, string? readAs = null, string kind = "game") => new(
        readAs ?? title, null, "model", "matched", [new GroundedCandidate(title, kind, "game_cartridge", null, 1996, 0.95)], "T1");

    /// <summary>What the eval row shows (each item's release) and the phone reads it leaves out (Vision's and the model's whose release it does not show).</summary>
    private static (List<string> Shown, HashSet<string> LeftOut) ByTheEval()
    {
        GroundedItem[] vision = [Game(Harbor), Game(Meadow, readAs: "Copper"), Game(Lantern, kind: "book"), Game(Orchard)];
        GroundedItem[] model = [Game(Meadow) with { Unconfirmed = true }];
        GroundedItem[] server = [Game(Harbor), Game(Delta)];
        var truths = new Dictionary<string, TruthEntry> { ["s1"] = new("s1", GoldenSets.Shelf, null, null, "shelf_segment") };
        GroundedPhoto Row(string engine, GroundedItem[] items) => new("s1", engine, null, "ok", items, 0, 1000, null, "nominal");

        var row = EvalCombination.OfServerRead(
            [Row(M3Gate.Engine, model), Row(M3Gate.VisionEngine, vision), Row(ServerEngines.Model, server)], truths).Single();

        var shown = row.Items.Select(i => i.Best!.Title).ToList();
        var leftOut = vision.Concat(model).Where(read => !shown.Contains(read.Best!.Title)).Select(read => read.ReadTitle!).ToHashSet();
        return (shown, leftOut);
    }

    [Fact]
    public async Task The_eval_row_and_the_endpoint_show_the_same_releases_and_leave_out_the_same_phone_reads()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync(Harbor, ScanData.Upc(9801));
        await CatalogAsync(Delta, ScanData.Upc(9802));
        await CatalogAsync(Orchard, ScanData.Upc(9803));
        await CatalogAsync(Meadow, ScanData.Upc(9804));
        await CatalogAsync(Lantern, ScanData.Upc(9805), ReleaseFormat.Paperback, WorkKind.Book);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var modelOnly = Read(title: Meadow, kind: "game", year: 1996);
        modelOnly["unconfirmed"] = true;
        var snap = await IdentifyShelfSnapAsync(
            editor,
            sessionId,
            "shelf_segment",
            Read(title: Harbor, kind: "game", year: 1996),
            Read(title: "Copper", kind: "game", year: 1996),
            Read(title: Lantern, kind: "book", year: 1996),
            Read(title: Orchard, kind: "game", year: 1996),
            modelOnly);
        Reader.Answer = _ => ScriptedReader.Found(Harbor, Delta);

        (await ReadShelfAsync(editor, sessionId, snap)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        await using var context = Db.CreateContext();
        var rows = await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().Where(c => c.SessionId == sessionId).OrderBy(c => c.Seq).ToListAsync();
        var standing = rows.Where(r => r.Decision != CandidateDecision.Rejected).ToList();
        var skipped = rows.Where(r => r.Decision == CandidateDecision.Rejected).Select(r => r.VisibleText!).ToHashSet();
        var (shown, leftOut) = ByTheEval();

        shown.ShouldBe([Harbor, Delta, Orchard], "the server's two, in order, then the Vision read the rule keeps");
        leftOut.ShouldBe(new HashSet<string> { "Copper", Lantern, Meadow }, ignoreOrder: true);
        standing.Select(r => ReleaseTitleOf(r)).Order().ShouldBe(shown.Order(), "the endpoint shows the same releases");
        skipped.ShouldBe(leftOut, ignoreOrder: true, "and skips the same phone reads, the model's own among them");
    }

    /// <summary>The title of the release a row shows first.</summary>
    private static string? ReleaseTitleOf(ScanCandidate row)
    {
        using var json = System.Text.Json.JsonDocument.Parse(row.AlternativesJson ?? "[]");
        return json.RootElement.EnumerateArray().FirstOrDefault() is { ValueKind: System.Text.Json.JsonValueKind.Object } best
            ? best.EnumerateObject().First(p => p.Name.Equals("title", StringComparison.OrdinalIgnoreCase)).Value.GetString()
            : null;
    }
}
