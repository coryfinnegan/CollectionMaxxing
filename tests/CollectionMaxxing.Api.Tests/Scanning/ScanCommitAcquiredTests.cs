using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning;

// Issue #441: adding an item stamps the day it was added, so the owner never types it.
public class ScanCommitAcquiredTests(ApiFixture api) : ScanTest(api)
{
    [Fact]
    public async Task Committed_items_are_acquired_the_day_they_are_added()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(44101);
        await CatalogAsync("Wave Race 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, code);
        candidates.ShouldAllBe(c => Decision(c) == "accepted");

        var commit = await CommitAsync(editor, sessionId);

        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(1);
        items.ShouldAllBe(i => i.AcquiredOn == DateOnly.FromDateTime(DateTime.UtcNow));
    }
}
