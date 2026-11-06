using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Evals;

// A run from before the phone sent each spine's lines still has the OCR lines and pieces of its Vision photo. Grounding the photo
// (EvalRunScoring.GroundPhotoAsync, which every way of making a run goes through, a re-ground of an old run included) rebuilds each
// read's lines from them, so the spine whose chosen line found nothing is asked with another line. Neutral titles throughout.
public class EvalGroundPhotoLinesTests
{
    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    private static readonly string[] OcrText = ["XQZV WRTK", "IRON FOX", "MARA QUILLON", "ALPHA QUEST", "BETA QUEST", "GAMMA QUEST", "DELTA QUEST"];

    private static EvalRunScoring Scoring(Providers providers)
    {
        var scanning = new ScanningOptions { JobResolveBudget = TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, new Catalogue(), [providers], scanning, NullLogger<TextSearchResolver>.Instance);
        var grounder = new EvalGrounder(
            new IdentifyGrounding(resolver, new FixedScoring()), null!, scanning,
            Options.Create(new EvalsOptions { GroundingParallelism = 4 }), new NoSerials(), TimeProvider.System);
        return new EvalRunScoring(null!, null!, grounder, Options.Create(new EvalsOptions()), new FixedScoring(), NullLogger<EvalRunScoring>.Instance);
    }

    private static EvalReadItemInput Spine(string text, string? title = null) => new(
        new IdentifyItemInput(null, null, text, title ?? text, null, null, null, WorkKind.Book, null, null, null, null),
        null);

    private static EvalPhotoInput Vision(string firstText) => new(
        "p1", ScanEngine.VisionPipeline, EvalOutcome.Ok, null,
        [Spine(firstText, "XQZV WRTK"), Spine("ALPHA QUEST"), Spine("BETA QUEST"), Spine("GAMMA QUEST"), Spine("DELTA QUEST")], 0, 1000, null, ThermalState.Nominal,
        OcrLines: [.. OcrText.Select(text => new EvalOcrLineInput(text, 0.1, 0.1, 0.05, 0.8))],
        OcrPieces: [[0, 1, 2], [3], [4], [5], [6]]);

    private static Providers Knowing() => new(false, [Known("Iron Fox", "b1") with { Creators = ["Mara Quillon"] }, Known("Alpha Quest", "b2"), Known("Beta Quest", "b3"), Known("Gamma Quest", "b4"), Known("Delta Quest", "b5")]);

    [Fact]
    public async Task A_run_whose_vision_items_carry_no_lines_is_grounded_with_the_lines_rebuilt_from_its_pieces()
    {
        var grounded = await Scoring(Knowing()).GroundPhotoAsync([Vision("XQZV WRTK IRON FOX MARA QUILLON")], new EvalGroundingMemo(), CancellationToken.None);

        var revived = grounded.Single().Items.Where(i => i.Verdict == "ambiguous" && i.FoundByLine).ShouldHaveSingleItem();
        revived.Candidates.Select(c => c.Title).ShouldBe(["Iron Fox"]);
    }

    [Fact]
    public async Task A_run_whose_pieces_do_not_line_up_with_its_reads_is_grounded_as_before()
    {
        var photo = Vision("XQZV WRTK IRON FOX MARA QUILLON") with { OcrPieces = [[0, 1], [3], [4], [5], [6]] };

        var grounded = await Scoring(Knowing()).GroundPhotoAsync([photo], new EvalGroundingMemo(), CancellationToken.None);

        grounded.Single().Items.ShouldNotContain(i => i.FoundByLine);
        grounded.Single().Items.ShouldNotContain(i => i.ReadTitle == "XQZV WRTK");
    }
}
