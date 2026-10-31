using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>
/// The checks on one photo of an eval run: what is cleaned (a box a hair outside 0 to 1, a product code of the wrong shape, items past
/// the limit) and what is a problem. A problem skips that photo; it never refuses the run.
/// </summary>
public class EvalPhotoChecksTests
{
    private static EvalReadItemInput Item(string title, string? productCode = null) =>
        new(new IdentifyItemInput(null, null, title, title, null, null, null, null, null, null, null, null, productCode), null);

    private static EvalPhotoInput Photo(
        ScanEngine engine = ScanEngine.VisionPipeline,
        IReadOnlyList<EvalReadItemInput>? items = null,
        IReadOnlyList<EvalOcrLineInput>? lines = null,
        IReadOnlyList<IReadOnlyList<int>>? pieces = null) =>
        new("s1", engine, EvalOutcome.Ok, null, items ?? [Item("Dune")], 0, 1500, 800, ThermalState.Nominal, OcrLines: lines, OcrPieces: pieces);

    private static EvalOcrLineInput Line(double x = 0.1, double y = 0.1, double width = 0.05, double height = 0.5, string text = "Dune") =>
        new(text, x, y, width, height);

    [Fact]
    public void A_good_photo_passes_unchanged()
    {
        var photo = Photo(lines: [Line()], pieces: [[0]]);

        var check = EvalPhotoChecks.Check(photo, 0);

        check.Problem.ShouldBeNull();
        check.Note.ShouldBeNull();
        check.Photo!.OcrLines.ShouldBe(photo.OcrLines!);
        check.Photo.Items!.Select(i => i.Read.Title).ShouldBe(["Dune"]);
        check.Label.ShouldBe("s1");
    }

    [Theory]
    [InlineData(-0.000001, 0.0)]
    [InlineData(-0.01, 0.0)]
    [InlineData(1.000001, 1.0)]
    [InlineData(1.01, 1.0)]
    [InlineData(0.5, 0.5)]
    public void An_OCR_box_value_within_a_hundredth_outside_0_to_1_is_clamped_into_range(double sent, double kept)
    {
        var check = EvalPhotoChecks.Check(Photo(lines: [Line(x: sent, y: sent, width: sent, height: sent)]), 0);

        check.Problem.ShouldBeNull();
        var line = check.Photo!.OcrLines!.Single();
        (line.X, line.Y, line.Width, line.Height).ShouldBe((kept, kept, kept, kept));
    }

    [Theory]
    [InlineData(-0.02)]
    [InlineData(1.02)]
    [InlineData(2)]
    public void An_OCR_box_value_further_outside_than_that_is_a_problem_for_that_photo(double sent)
    {
        var check = EvalPhotoChecks.Check(Photo(lines: [Line(y: sent)]), 3);

        check.Photo.ShouldBeNull();
        check.Problem!.Code.ShouldBe("evals.ocr_line_invalid");
        check.Problem.Field.ShouldBe("results[3].ocrLines[0]");
    }

    [Fact]
    public void An_OCR_line_with_no_box_value_or_no_text_is_a_problem()
    {
        EvalPhotoChecks.Check(Photo(lines: [new EvalOcrLineInput("Dune", 0.1, 0.1, 0.1, null)]), 0).Problem!.Code.ShouldBe("evals.ocr_line_invalid");
        EvalPhotoChecks.Check(Photo(lines: [new EvalOcrLineInput(null, 0.1, 0.1, 0.1, 0.1)]), 0).Problem!.Code.ShouldBe("evals.ocr_line_invalid");
        EvalPhotoChecks.Check(Photo(lines: [Line(text: new string('t', 201))]), 0).Problem!.Code.ShouldBe("evals.ocr_line_invalid");
    }

    [Fact]
    public void OCR_lines_sent_with_another_engines_photo_are_not_looked_at()
    {
        var check = EvalPhotoChecks.Check(Photo(ScanEngine.AppleModel, lines: [Line(x: 7)]), 0);

        check.Problem.ShouldBeNull();
    }

    [Fact]
    public void An_invalid_product_code_is_dropped_and_the_item_stays()
    {
        var check = EvalPhotoChecks.Check(Photo(items: [Item("Star Fox 64", "!!! not a code !!!"), Item("Wave Race 64", "SNS-ADNE-USA"), Item("Dune")]), 0);

        check.Problem.ShouldBeNull();
        check.Photo!.Items!.Select(i => i.Read.ProductCode).ShouldBe([null, "SNS-ADNE-USA", null]);
        check.Photo.Items!.Select(i => i.Read.Title).ShouldBe(["Star Fox 64", "Wave Race 64", "Dune"]);
    }

    [Fact]
    public void Items_past_the_limit_are_cut_to_the_first_ones_and_the_cut_is_the_note()
    {
        var items = Enumerable.Range(0, 150).Select(i => Item($"Book {i}")).ToList();

        var check = EvalPhotoChecks.Check(Photo(items: items), 0);

        check.Problem.ShouldBeNull();
        check.Photo!.Items!.Count.ShouldBe(120);
        check.Photo.Items![0].Read.Title.ShouldBe("Book 0");
        check.Photo.Items![119].Read.Title.ShouldBe("Book 119");
        check.Note.ShouldBe("trimmed_items_120_of_150");
    }

    [Fact]
    public void A_shelf_of_46_books_is_not_cut()
    {
        var check = EvalPhotoChecks.Check(Photo(items: [.. Enumerable.Range(0, 46).Select(i => Item($"Book {i}"))]), 0);

        check.Photo!.Items!.Count.ShouldBe(46);
        check.Note.ShouldBeNull();
    }

    [Fact]
    public void A_tier_that_is_not_T0_T1_or_T2_is_a_problem_for_that_photo()
    {
        var bad = new EvalReadItemInput(Item("Dune").Read, null, (ScanTier)7);

        var check = EvalPhotoChecks.Check(Photo(items: [bad]), 2);

        check.Photo.ShouldBeNull();
        check.Problem!.Code.ShouldBe("evals.tier_invalid");
    }

    [Fact]
    public void An_item_over_16_KB_of_text_is_a_problem_for_that_photo()
    {
        var check = EvalPhotoChecks.Check(Photo(items: [Item(new string('x', 17 * 1024))]), 0);

        check.Photo.ShouldBeNull();
        check.Problem!.Code.ShouldBe("scan.item_too_large");
    }

    [Fact]
    public void A_photo_with_a_bad_id_is_labelled_by_its_place_in_the_run_so_the_id_never_reaches_a_file_or_a_log()
    {
        var check = EvalPhotoChecks.Check(Photo() with { PhotoId = "..\\x" }, 4);

        check.Problem!.Code.ShouldBe("evals.photo_id_invalid");
        check.Label.ShouldBe("results[4]");
        EvalPhotoChecks.Check(null, 5).Label.ShouldBe("results[5]");
    }

    [Fact]
    public void A_negative_latency_is_a_problem_for_that_photo()
    {
        EvalPhotoChecks.Check(Photo() with { LatencyMs = -5 }, 0).Problem!.Code.ShouldBe("evals.latency_invalid");
    }
}
