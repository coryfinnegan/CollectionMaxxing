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

// A read /identify skips (a shelf spine the catalogue found nothing for) is never scored, but it still holds its place while the
// places are settled, and drops only afterwards. These tests ground both engines' rows of a printed shelf through the run's own
// grounding (EvalRunScoring.GroundPhotoAsync) and build the combined row from them. Neutral titles throughout.
public class EvalCombinedSkippedReadsTests
{
    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    private static readonly TruthEntry Truth = new("p1", GoldenSets.Shelf, null, null, null);

    private static readonly IReadOnlyDictionary<string, TruthEntry> Truths = new Dictionary<string, TruthEntry> { ["p1"] = Truth };

    private static EvalRunScoring Scoring(Providers providers)
    {
        var scanning = new ScanningOptions { JobResolveBudget = TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, new Catalogue(), [providers], scanning, NullLogger<TextSearchResolver>.Instance);
        var grounder = new EvalGrounder(
            new IdentifyGrounding(resolver, new FixedScoring()), null!, scanning,
            Options.Create(new EvalsOptions { GroundingParallelism = 4 }), new NoSerials(), TimeProvider.System);
        return new EvalRunScoring(null!, null!, grounder, Options.Create(new EvalsOptions()), new FixedScoring(), NullLogger<EvalRunScoring>.Instance);
    }

    private static EvalReadItemInput Spine(string title, string segment, double x, double width = 0.05) => new(
        new IdentifyItemInput(null, segment, title, title, null, null, null, WorkKind.Book, null, null, null, null, Box: new ReadBoxInput(x, 0.1, width, 0.8)),
        null);

    /// <summary>Three spines in places of their own, which both engines read and the providers know.</summary>
    private static EvalReadItemInput[] Fillers(string segment) =>
        [Spine("Alpha Quest", segment, 0.0), Spine("Beta Quest", segment, 0.15), Spine("Gamma Quest", segment, 0.3)];

    private static readonly EvalOcrLineInput[] Print =
        [new("Alpha Quest", 0.0, 0.1, 0.05, 0.8), new("Beta Quest", 0.15, 0.1, 0.05, 0.8), new("Gamma Quest", 0.3, 0.1, 0.05, 0.8)];

    private static EvalPhotoInput Row(ScanEngine engine, params EvalReadItemInput[] items) => new(
        "p1", engine, EvalOutcome.Ok, null, items, 0, 1000, null, ThermalState.Nominal,
        OcrLines: engine == ScanEngine.VisionPipeline ? Print : null);

    private static Providers Knowing(params string[] titles) =>
        new(false, [.. titles.Select((title, i) => Known(title, $"b{i}"))]);

    private static async Task<GroundedPhoto> CombinedAsync(Providers providers, EvalPhotoInput model, EvalPhotoInput vision)
    {
        var grounded = await Scoring(providers).GroundPhotoAsync([model, vision], new EvalGroundingMemo(), CancellationToken.None);
        return EvalCombination.Of(grounded, Truths).Single();
    }

    [Fact]
    public async Task A_right_model_read_beside_a_Vision_read_that_found_nothing_is_shown_though_the_print_does_not_carry_its_title()
    {
        var providers = Knowing("Alpha Quest", "Beta Quest", "Gamma Quest", "Amber Orchard");
        var vision = Row(ScanEngine.VisionPipeline, [.. Fillers("v"), Spine("ZXQV WRLKM", "v", 0.8)]);
        var model = Row(ScanEngine.AppleModel, [.. Fillers("m"), Spine("Amber Orchard", "m", 0.8)]);

        var combined = await CombinedAsync(providers, model, vision);

        combined.Items.ShouldContain(i => i.ReadTitle == "Amber Orchard", "Vision's read of that place was skipped, but it held the place the model's read contests");
        combined.Items.ShouldNotContain(i => i.ReadTitle == "ZXQV WRLKM");
    }

    [Fact]
    public async Task A_Vision_piece_over_two_model_reads_that_found_nothing_loses_its_place()
    {
        var providers = Knowing("Alpha Quest", "Beta Quest", "Gamma Quest", "Wide Piece");
        var vision = Row(ScanEngine.VisionPipeline, [.. Fillers("v"), Spine("Wide Piece", "v", 0.6, width: 0.35)]);
        var model = Row(ScanEngine.AppleModel, [.. Fillers("m"), Spine("ZXQV WRLKM", "m", 0.62), Spine("QPLN TRVX", "m", 0.85)]);

        var combined = await CombinedAsync(providers, model, vision);

        combined.Items.ShouldNotContain(i => i.ReadTitle == "Wide Piece", "a piece that holds two spines loses its place, as it does in /identify");
        combined.Items.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_rows_a_skipped_read_leaves_behind_are_not_scored_items_of_either_engine_row()
    {
        var providers = Knowing("Alpha Quest", "Beta Quest", "Gamma Quest");
        var vision = Row(ScanEngine.VisionPipeline, [.. Fillers("v"), Spine("ZXQV WRLKM", "v", 0.8)]);
        var model = Row(ScanEngine.AppleModel, Fillers("m"));

        var grounded = await Scoring(providers).GroundPhotoAsync([model, vision], new EvalGroundingMemo(), CancellationToken.None);

        var visionRow = grounded.Single(p => p.Engine == "vision_pipeline");
        visionRow.Items.Count.ShouldBe(3);
        visionRow.SkippedItems.ShouldHaveSingleItem().Skipped.ShouldBeTrue();
        EvalCombination.Of(grounded, Truths).Single().SkippedItems.ShouldBeNull();
    }
}
