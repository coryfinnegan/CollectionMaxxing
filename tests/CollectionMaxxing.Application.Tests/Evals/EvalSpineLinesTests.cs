using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Evals;

// A run from before the phone sent each spine's lines still has the OCR lines and pieces of its Vision photo; the PC rebuilds each
// Vision read's lines from them, but only when the reads and the pieces line up exactly. Names and titles are neutral.
public class EvalSpineLinesTests
{
    private static readonly string[] Lines = ["COPPER LANTERN", "TESS HARROWGATE", "QUIET", "HARBOR", "ALPHA QUEST"];

    private static readonly IReadOnlyList<IReadOnlyList<int>> Pieces = [[0, 1], [2, 3], [4]];

    private static EvalReadItemInput Read(string text, IReadOnlyList<string>? lines = null) => new(
        new IdentifyItemInput(null, null, text, text, null, null, null, null, null, null, null, null, Lines: lines), null);

    private static EvalPhotoInput Photo(
        ScanEngine engine, IReadOnlyList<EvalReadItemInput> items, IReadOnlyList<string>? ocr = null, IReadOnlyList<IReadOnlyList<int>>? pieces = null) => new(
        "shelf-01", engine, EvalOutcome.Ok, null, items, 0, 100, 10, ThermalState.Nominal,
        OcrLines: [.. (ocr ?? Lines).Select(text => new EvalOcrLineInput(text, 0.1, 0.1, 0.1, 0.1))],
        OcrPieces: pieces ?? Pieces);

    private static IReadOnlyList<EvalReadItemInput> Reads() =>
        [Read("COPPER LANTERN TESS HARROWGATE"), Read("QUIET HARBOR"), Read("ALPHA QUEST")];

    [Fact]
    public void A_run_from_before_the_phone_sent_lines_gets_each_vision_reads_lines_from_its_piece()
    {
        var rebuilt = EvalSpineLines.Rebuild(Photo(ScanEngine.VisionPipeline, Reads()));

        rebuilt.Items![0].Read.LineList().ShouldBe(["COPPER LANTERN", "TESS HARROWGATE"]);
        rebuilt.Items[1].Read.LineList().ShouldBe(["QUIET", "HARBOR"]);
        rebuilt.Items[2].Read.LineList().ShouldBe(["ALPHA QUEST"]);
    }

    [Fact]
    public void Lines_are_not_rebuilt_when_the_number_of_reads_and_pieces_differ()
    {
        var photo = Photo(ScanEngine.VisionPipeline, [Read("COPPER LANTERN TESS HARROWGATE"), Read("QUIET HARBOR")]);

        EvalSpineLines.Rebuild(photo).ShouldBeSameAs(photo);
    }

    [Fact]
    public void Lines_are_not_rebuilt_when_a_reads_text_is_not_its_pieces_lines_joined()
    {
        var photo = Photo(ScanEngine.VisionPipeline, [Read("COPPER LANTERN TESS HARROWGATE"), Read("QUIET HARBOUR"), Read("ALPHA QUEST")]);

        EvalSpineLines.Rebuild(photo).ShouldBeSameAs(photo);
    }

    [Fact]
    public void Lines_the_phone_sent_are_kept_and_other_engines_rows_get_none()
    {
        var sent = Photo(ScanEngine.VisionPipeline, [Read("COPPER LANTERN TESS HARROWGATE", ["COPPER LANTERN"]), Read("QUIET HARBOR"), Read("ALPHA QUEST")]);
        var model = Photo(ScanEngine.AppleModel, Reads());

        EvalSpineLines.Rebuild(sent).ShouldBeSameAs(sent);
        EvalSpineLines.Rebuild(model).ShouldBeSameAs(model);
    }

    [Fact]
    public void A_piece_pointing_at_a_line_that_is_not_there_rebuilds_nothing()
    {
        var photo = Photo(ScanEngine.VisionPipeline, Reads(), pieces: [[0, 1], [2, 3], [9]]);

        EvalSpineLines.Rebuild(photo).ShouldBeSameAs(photo);
    }
}

internal static class ReadLines
{
    public static IReadOnlyList<string> LineList(this IdentifyItemInput read) => read.Lines ?? [];
}
