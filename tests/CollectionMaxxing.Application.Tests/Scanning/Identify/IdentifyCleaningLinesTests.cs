using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// The lines of a spine's piece the phone sends are cleaned like every other field, capped, and counted in the item's size.
public class IdentifyCleaningLinesTests
{
    [Fact]
    public void Lines_lose_control_characters_and_empty_and_repeated_lines_and_keep_their_order()
    {
        var kept = SpineLines.Clean(["COPPER\u0007 LANTERN", "  ", "TESS HARROWGATE", "tess harrowgate", "", "TESS HARROWGATE"]);

        SpineLines.Split(kept).ShouldBe(["COPPER LANTERN", "TESS HARROWGATE"]);
    }

    [Fact]
    public void A_line_with_a_newline_in_it_stays_one_line()
    {
        SpineLines.Split(SpineLines.Clean(["COPPER\nLANTERN"])).ShouldBe(["COPPER LANTERN"]);
    }

    [Fact]
    public void Each_line_is_cut_to_the_field_length_and_at_most_twelve_lines_are_kept()
    {
        var lines = new[] { new string('x', 900) }.Concat(Enumerable.Range(1, 20).Select(i => $"LINE NUMBER {i}")).ToList();

        var kept = SpineLines.Split(SpineLines.Clean(lines));

        kept.Count.ShouldBe(12);
        kept[0].Length.ShouldBe(500);
        kept[1].ShouldBe("LINE NUMBER 1");
    }

    [Fact]
    public void A_read_with_no_lines_has_none()
    {
        SpineLines.Clean(null).ShouldBeNull();
        SpineLines.Clean([" ", ""]).ShouldBeNull();
    }

    [Fact]
    public void Lines_count_in_the_size_of_the_item()
    {
        var read = new IdentifyItemInput(
            null, null, "Copper Lantern", "Copper Lantern", null, null, null, WorkKind.Book, null, null, null, null,
            Lines: [.. Enumerable.Range(0, 40).Select(_ => new string('x', 500))]); // 20 KB sent, whatever would be kept
        var photo = new EvalPhotoInput("shelf-01", ScanEngine.VisionPipeline, EvalOutcome.Ok, null, [new EvalReadItemInput(read, null)], 0, 100, 10, ThermalState.Nominal);

        var check = EvalPhotoChecks.Check(photo, 0);

        check.Photo.ShouldBeNull();
        check.Problem!.Code.ShouldBe("scan.item_too_large");
    }
}
