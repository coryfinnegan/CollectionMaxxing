using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// What a shelf read leaves behind and what it says: the picture is never stored, the logs name no title and no line of print,
// and the recognition config tells the phone whether to send a picture at all.
public class ShelfReadHygieneTests(ApiFixture api) : ShelfReadTest(api)
{
    private const string Title = "Zephyrine Quillmarch Odyssey";
    private const string PrintedLine = "QUIET UNIQUE PRINT LINE 77";

    private string CoversRoot { get; } = Path.Combine(Path.GetTempPath(), "cm-shelf-read-" + Guid.NewGuid().ToString("N"));

    public override ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(CoversRoot))
            {
                Directory.Delete(CoversRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }

        GC.SuppressFinalize(this);
        return base.DisposeAsync();
    }

    private async Task<(HttpClient Client, Guid SessionId, Guid SnapId)> ArrangeAsync()
    {
        Settings["Catalog:CoversRoot"] = CoversRoot;
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(9501));
        await CatalogAsync(Title, ScanData.Upc(9502));
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);
        var snap = await IdentifyShelfSnapAsync(client, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", year: 1996));
        Reader.Answer = _ => ScriptedReader.Found(Title);
        return (client, sessionId, snap);
    }

    private static HashSet<string> SpooledFiles() => Directory.Exists(Path.GetTempPath())
        ? Directory.EnumerateFiles(Path.GetTempPath(), "ASPNETCORE_*").ToHashSet()
        : [];

    [Fact]
    public async Task The_photo_is_never_stored()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        var before = SpooledFiles();

        var added = await CandidatesOfAsync(await ReadShelfAsync(client, sessionId, snap, lines: [PrintedLine, Title]));

        added.Count.ShouldBe(1);
        Photos.ReceivedCalls().ShouldBeEmpty("no call to the photo store");
        (Directory.Exists(CoversRoot) ? Directory.GetFiles(CoversRoot, "*", SearchOption.AllDirectories) : []).ShouldBeEmpty();
        SpooledFiles().Except(before).ShouldBeEmpty("the body is never spooled to a temporary file");
        await using var context = Db.CreateContext();
        (await context.Assets.IgnoreQueryFilters().CountAsync()).ShouldBe(0, "no asset row holds the picture");
        (await context.ScanSnaps.IgnoreQueryFilters().CountAsync(s => s.AssetId != null)).ShouldBe(0);
    }

    [Fact]
    public async Task Logs_never_carry_a_title_or_a_line_of_print()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;

        await ReadShelfAsync(client, sessionId, snap, lines: [PrintedLine, Title]);
        var refused = await ReadShelfAsync(client, sessionId, Guid.NewGuid(), lines: [PrintedLine]);
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var lines = Logs.Lines.Where(l => l.Contains("Shelf read", StringComparison.Ordinal)).ToList();
        lines.ShouldNotBeEmpty("the read is logged");
        lines.ShouldContain(l => l.Contains("ended ok", StringComparison.Ordinal) && l.Contains("HTTP 200", StringComparison.Ordinal) && l.Contains("1 items", StringComparison.Ordinal));
        lines.ShouldContain(l => l.Contains("scan.snap_not_found", StringComparison.Ordinal) && l.Contains("HTTP 404", StringComparison.Ordinal));
        Logs.Lines.ShouldNotContain(l => l.Contains(Title, StringComparison.OrdinalIgnoreCase), "no log line of any kind names the title the reader read");
        Logs.Lines.ShouldNotContain(l => l.Contains("Quillmarch", StringComparison.OrdinalIgnoreCase));
        Logs.Lines.ShouldNotContain(l => l.Contains(PrintedLine, StringComparison.OrdinalIgnoreCase));
        Logs.Lines.ShouldNotContain(l => l.Contains("unique print", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Recognition_config_says_whether_the_server_reads_shelves()
    {
        var withReader = await ConfigAsync(AsUser(Guid.CreateVersion7())); // this app has a reader endpoint
        var withoutReader = await ConfigAsync(Api.CreateClient(Guid.CreateVersion7())); // the fixture's own app has none

        withReader.GetProperty("shelfReader").GetBoolean().ShouldBeTrue();
        withoutReader.GetProperty("shelfReader").GetBoolean().ShouldBeFalse();
        withReader.GetProperty("version").GetString().ShouldNotBe(withoutReader.GetProperty("version").GetString(), "the flag is part of what the phone caches by");
    }

    private static async Task<JsonElement> ConfigAsync(HttpClient client)
    {
        using var _ = client;
        var response = await client.GetAsync("/api/v1/recognition/config");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }
}
