using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.ShelfReading;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The rule that picks what the server's shelf read adds (<see cref="PcShelfUpgrade"/>) is the one the <c>server_vision</c> row is built with, so the eval measures
/// what the app shows. One fixture goes through both paths: the eval's combination of the server's reading with Vision's row, and the rule asked the way the
/// scan endpoint asks it (the rows the snap has, the read's own best release, the print). Neutral titles throughout.
/// </summary>
public sealed class PcShelfUpgradeParityTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private static readonly string[] Print = ["Lantern Row", "Quiet Harbor", "Paper Moon"];

    private static GroundedPhoto Shelf(string engine, params GroundedItem[] items) => Photo("s1", engine, items);

    private static GroundedPhoto Printing(GroundedPhoto vision, params string[] lines) =>
        vision with { OcrLines = [.. lines.Select((text, i) => new GroundedOcrLine(text, 0.05 * i, 0.1, 0.04, 0.02))] };

    private IReadOnlyDictionary<string, TruthEntry> Truths()
    {
        _golden.AddShelf("s1", new[] { "Quiet Harbor", "Lantern Row", "Paper Moon" }.Select(t => ShelfItem(t)));
        return TruthsOf(_golden);
    }

    private static PcShelfUpgrade.ReleaseKey? KeyOf(GroundedItem item) => item.Best is { } best ? PcShelfUpgrade.ReleaseKey.Of(best.Title, best.Year) : null;

    /// <summary>The titles the eval's server_vision row shows that the server's reading alone brought (the rows marked unconfirmed).</summary>
    private HashSet<string?> AddedByTheEvalRow(GroundedPhoto vision, GroundedItem[] server) =>
        EvalCombination.OfServerRead([Shelf(ServerEngines.Model, server), vision], Truths())
            .Single().Items.Where(item => item.Unconfirmed).Select(item => item.ReadTitle).ToHashSet();

    /// <summary>The titles the scan endpoint's rule lets stand for the same readings.</summary>
    private static HashSet<string?> AddedByTheRule(GroundedPhoto vision, GroundedItem[] server)
    {
        var printed = PcShelfUpgrade.PrintOf(vision.OcrLines?.Select(line => line.Text));
        var existing = vision.Items.Select(KeyOf).ToList();
        return server.Where(read => PcShelfUpgrade.Stands(KeyOf(read), read.ReadTitle, existing, printed)).Select(read => read.ReadTitle).ToHashSet();
    }

    private static GroundedItem[] ServerReads() =>
    [
        T1(Matched("Quiet Harbor")), // the release Vision's row already has
        T1(Matched("Paper Moon")), // printed on the shelf
        T1(Matched("Zebra Opus", readAs: "Zebra Opus")), // the print does not carry it
    ];

    [Fact]
    public void With_a_print_the_eval_row_and_the_rule_add_the_same_titles()
    {
        var vision = Printing(Shelf(Vision, Kept(Matched("Lantern Row")), Kept(Matched("Quiet Harbor"))), Print);

        var byEval = AddedByTheEvalRow(vision, ServerReads());
        var byRule = AddedByTheRule(vision, ServerReads());

        byRule.ShouldBe(new HashSet<string?> { "Paper Moon" });
        byEval.ShouldBe(byRule);
    }

    [Fact]
    public void With_no_print_the_eval_row_and_the_rule_add_every_read_that_is_not_a_repeat()
    {
        var vision = Shelf(Vision, Kept(Matched("Lantern Row")), Kept(Matched("Quiet Harbor")));

        var byEval = AddedByTheEvalRow(vision, ServerReads());
        var byRule = AddedByTheRule(vision, ServerReads());

        byRule.ShouldBe(new HashSet<string?> { "Paper Moon", "Zebra Opus" });
        byEval.ShouldBe(byRule);
    }

    [Fact]
    public void A_server_read_of_the_same_title_in_another_year_is_another_release_in_both_paths()
    {
        var vision = Printing(Shelf(Vision, Kept(Matched("Quiet Harbor"))), "Quiet Harbor");
        var sequel = T1(Matched("Quiet Harbor") with { Candidates = [Release("Quiet Harbor") with { Year = 2004 }] });

        var byEval = AddedByTheEvalRow(vision, [sequel]);
        var byRule = AddedByTheRule(vision, [sequel]);

        byRule.Count.ShouldBe(1, "a different year is a different release");
        byEval.ShouldBe(byRule);
    }

    [Fact]
    public void A_read_whose_release_the_catalogue_did_not_find_is_held_to_the_print_only()
    {
        var vision = Printing(Shelf(Vision, Kept(Matched("Lantern Row"))), "Lantern Row", "Paper Moon");
        GroundedItem[] server = [T1(NotFound("Paper Moon")), T1(NotFound("Zebra Opus"))];

        var byRule = AddedByTheRule(vision, server);

        byRule.ShouldBe(new HashSet<string?> { "Paper Moon" });
    }
}
