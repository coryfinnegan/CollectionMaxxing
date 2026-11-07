using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A read the providers ran out of time for is stored and asked again by the pending job, whose ladder ends with the other lines of the
// spine, so a stored spine read keeps its lines. A row stored before the phone sent lines has none.
public class StoredReadLinesTests
{
    private static ReadFacts Spine(string? lines) => new(
        "XQZV WRTK", "XQZV WRTK COPPER LANTERN", null, null, null, WorkKind.Book, null, null, null, Lines: lines);

    private static ScanCandidate Stored(string inferredJson) => new() { VisibleText = "XQZV WRTK COPPER LANTERN", InferredJson = inferredJson };

    [Fact]
    public void A_stored_spine_read_keeps_its_lines_for_the_pending_jobs_ask()
    {
        var json = StoredRead.Serialize(Spine("XQZV WRTK\nCOPPER LANTERN\nTESS HARROWGATE"), ScoringProfileKind.ShelfSpine);

        var (facts, profile) = StoredRead.From(Stored(json));

        profile.ShouldBe(ScoringProfileKind.ShelfSpine);
        facts.LineList.ShouldBe(["XQZV WRTK", "COPPER LANTERN", "TESS HARROWGATE"]);
    }

    [Fact]
    public void A_row_stored_before_the_phone_sent_lines_still_reads_back_with_none()
    {
        var old = """{"title":"XQZV WRTK","kind":"book","profile":"ShelfSpine"}""";

        var (facts, _) = StoredRead.From(Stored(old));

        facts.Title.ShouldBe("XQZV WRTK");
        facts.Lines.ShouldBeNull();
        StoredRead.Serialize(Spine(null), ScoringProfileKind.ShelfSpine).ShouldNotContain("lines");
    }

    [Fact]
    public void A_stored_read_that_is_not_a_spine_reads_back_with_no_lines()
    {
        var json = StoredRead.Serialize(Spine("XQZV WRTK\nCOPPER LANTERN"), ScoringProfileKind.Cover);

        StoredRead.From(Stored(json)).Facts.Lines.ShouldBeNull();
    }
}
