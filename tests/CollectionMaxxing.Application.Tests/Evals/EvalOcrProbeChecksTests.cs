using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>
/// The OCR probes the phone sends with the Vision photo of a shelf: extra reads of the same picture (another recognition level, language
/// correction, tiles), kept for a replay and never scored. They are checked like the OCR lines are: a hair outside 0 to 1 is clamped in,
/// anything else outside the limits is a problem for that photo, and any engine but Vision's has none.
/// </summary>
public class EvalOcrProbeChecksTests
{
    private static EvalPhotoInput Photo(ScanEngine engine = ScanEngine.VisionPipeline, IReadOnlyList<EvalOcrProbeInput?>? probes = null) =>
        new(
            "s1", engine, EvalOutcome.Ok, null,
            [new EvalReadItemInput(new IdentifyItemInput(null, null, "Dune", "Dune", null, null, null, null, null, null, null, null, null), null)],
            0, 1500, 800, ThermalState.Nominal, OcrProbes: probes!);

    private static EvalOcrProbeLineInput Line(
        string? text = "DUNE", double? confidence = 0.9, int? pass = 0, double? x = 0.1, double? y = 0.1, double? width = 0.05, double? height = 0.5) =>
        new(text, confidence, pass, x, y, width, height);

    private static EvalOcrProbeInput Probe(string? name = "production", long? ms = 400, params EvalOcrProbeLineInput[] lines) => new(name, ms, [.. lines]);

    /// <summary>One probe with one line, for a test about the line.</summary>
    private static EvalOcrProbeInput[] Probes(EvalOcrProbeLineInput line) => [Probe("production", 400, line)];

    [Fact]
    public void A_photo_without_probes_passes_as_it_was_sent()
    {
        var check = EvalPhotoChecks.Check(Photo(), 0);

        check.Problem.ShouldBeNull();
        check.Photo!.OcrProbes.ShouldBeNull();
    }

    [Fact]
    public void Probes_within_the_limits_are_taken_as_they_were_sent()
    {
        EvalOcrProbeInput[] probes =
        [
            Probe("production", 410, Line(pass: 0), Line(pass: 90), Line(pass: 180, confidence: 0.1), Line(pass: 270)),
            Probe("fast0", 60, Line(text: "TWO", confidence: 0)),
            Probe("corrected0", 120, Line(confidence: 1)),
            Probe("tiles0", 300),
        ];

        var check = EvalPhotoChecks.Check(Photo(probes: probes), 0);

        check.Problem.ShouldBeNull();
        var taken = check.Photo!.OcrProbes!;
        taken.Select(p => p.Name).ShouldBe(["production", "fast0", "corrected0", "tiles0"]);
        taken.Select(p => p.Ms).ShouldBe([410L, 60L, 120L, 300L]);
        for (var i = 0; i < probes.Length; i++)
        {
            taken[i].Lines!.ShouldBe(probes[i].Lines!); // the lines are records: equal when every field is
        }
    }

    [Fact]
    public void Four_probes_of_fifteen_hundred_lines_each_are_taken_and_a_line_of_two_hundred_characters_is_kept()
    {
        var lines = Enumerable.Range(0, 1500).Select(i => Line(text: i == 0 ? new string('t', 200) : $"Line {i}")).ToArray();

        var check = EvalPhotoChecks.Check(Photo(probes: [Probe("a", 1, lines), Probe("b", 1, lines), Probe("c", 1, lines), Probe("d", 1, lines)]), 0);

        check.Problem.ShouldBeNull();
        check.Photo!.OcrProbes!.Select(p => p.Lines!.Count).ShouldBe([1500, 1500, 1500, 1500]);
    }

    [Theory]
    [InlineData(-0.000001, 0.0)]
    [InlineData(-0.01, 0.0)]
    [InlineData(1.000001, 1.0)]
    [InlineData(1.01, 1.0)]
    [InlineData(0.5, 0.5)]
    public void A_box_value_or_confidence_within_a_hundredth_outside_0_to_1_is_clamped_into_range(double sent, double kept)
    {
        var check = EvalPhotoChecks.Check(Photo(probes: Probes(Line(confidence: sent, x: sent, y: sent, width: sent, height: sent))), 0);

        check.Problem.ShouldBeNull();
        var clamped = check.Photo!.OcrProbes!.Single().Lines!.Single();
        (clamped.Confidence, clamped.X, clamped.Y, clamped.Width, clamped.Height).ShouldBe((kept, kept, kept, kept, kept));
    }

    [Fact]
    public void A_time_of_0_and_of_an_hour_are_taken()
    {
        EvalPhotoChecks.Check(Photo(probes: [Probe(ms: 0, lines: Line())]), 0).Problem.ShouldBeNull();
        EvalPhotoChecks.Check(Photo(probes: [Probe(ms: 3_600_000, lines: Line())]), 0).Problem.ShouldBeNull();
    }

    public static TheoryData<string, EvalOcrProbeInput?[]> BadProbes() => new()
    {
        { "five probes", [.. Enumerable.Range(0, 5).Select(i => Probe($"p{i}", 1, Line()))] },
        { "1501 lines", [Probe("production", 1, [.. Enumerable.Range(0, 1501).Select(i => Line(text: $"Line {i}"))])] },
        { "text of 201 characters", Probes(Line(text: new string('t', 201))) },
        { "no text", Probes(Line(text: null)) },
        { "x over one", Probes(Line(x: 1.5)) },
        { "y below zero", Probes(Line(y: -0.5)) },
        { "width over one", Probes(Line(width: 2)) },
        { "no height", Probes(Line(height: null)) },
        { "confidence over one", Probes(Line(confidence: 1.5)) },
        { "confidence below zero", Probes(Line(confidence: -0.5)) },
        { "no confidence", Probes(Line(confidence: null)) },
        { "pass of 45", Probes(Line(pass: 45)) },
        { "pass of 360", Probes(Line(pass: 360)) },
        { "negative pass", Probes(Line(pass: -90)) },
        { "no pass", Probes(Line(pass: null)) },
        { "negative time", [Probe(ms: -1, lines: Line())] },
        { "time over an hour", [Probe(ms: 3_600_001, lines: Line())] },
        { "no time", [Probe(ms: null, lines: Line())] },
        { "no name", [Probe(name: null, lines: Line())] },
        { "blank name", [Probe(name: "  ", lines: Line())] },
        { "name of 65 characters", [Probe(name: new string('n', 65), lines: Line())] },
        { "no lines list", [new EvalOcrProbeInput("production", 400, null)] },
        { "an empty probe", [null] },
        { "an empty line", [new EvalOcrProbeInput("production", 400, [null!])] },
    };

    [Theory]
    [MemberData(nameof(BadProbes))]
    public void A_probe_over_a_limit_is_a_problem_for_that_photo(string because, EvalOcrProbeInput?[] probes)
    {
        var check = EvalPhotoChecks.Check(Photo(probes: probes), 3);

        check.Photo.ShouldBeNull(because);
        check.Problem!.Code.ShouldBe("evals.ocr_probe_invalid", because);
        check.Problem.Field.ShouldStartWith("results[3].ocrProbes");
    }

    [Fact]
    public void The_problem_names_the_probe_and_the_line()
    {
        var check = EvalPhotoChecks.Check(Photo(probes: [Probe("production", 1, Line()), Probe("fast0", 1, Line(), Line(pass: 45))]), 2);

        check.Problem!.Field.ShouldBe("results[2].ocrProbes[1].lines[1]");
    }

    [Theory]
    [InlineData(ScanEngine.AppleModel)]
    [InlineData(ScanEngine.FmText)]
    public void Probes_sent_with_another_engines_photo_are_dropped_unlooked_at(ScanEngine engine)
    {
        var tooMany = Enumerable.Range(0, 9).Select(i => Probe($"p{i}", -5, Line(x: 7))).ToArray();

        var check = EvalPhotoChecks.Check(Photo(engine, tooMany), 0);

        check.Problem.ShouldBeNull();
        check.Photo!.OcrProbes.ShouldBeNull();
    }

    [Fact]
    public void Probes_do_not_change_how_the_OCR_lines_are_checked()
    {
        var photo = Photo(probes: [Probe(lines: Line())]) with { OcrLines = [new EvalOcrLineInput("Dune", 1.01, 0.1, 0.1, 0.1)] };

        var check = EvalPhotoChecks.Check(photo, 0);

        check.Problem.ShouldBeNull();
        check.Photo!.OcrLines!.Single().X.ShouldBe(1.0);
        check.Photo.OcrProbes!.Single().Lines!.Single().X.ShouldBe(0.1);
    }
}
