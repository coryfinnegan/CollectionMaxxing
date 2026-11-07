using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Evals;

// The app sends one identify per snap holding both engines' reads, so a console printed on a line of one engine's read is printed for the
// whole shot. Grounding a photo (EvalRunScoring.GroundPhotoAsync) tells each row's reads that, the way /identify's one cleaning does.
// Neutral titles throughout.
public class EvalGroundPhotoPrintedPlatformTests
{
    private const string Title = "Quiet Harbor Atlas";

    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    /// <summary>A provider of every kind that knows the title as a book and as a PS2 game, the book first.</summary>
    private sealed class BookAndGame : ICatalogProvider
    {
        public string Name => "tie";

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderRelease>>(
                string.Equals(query.Title, Title, StringComparison.OrdinalIgnoreCase)
                    ?
                    [
                        new ProviderRelease { Provider = "tie", ExternalId = "b1", Title = Title, Kind = WorkKind.Book, Year = 2001 },
                        new ProviderRelease { Provider = "tie", ExternalId = "g1", Title = Title, Kind = WorkKind.Game, Platform = "PS2", Year = 2002 },
                    ]
                    : []);
    }

    private static EvalRunScoring Scoring()
    {
        var scanning = new ScanningOptions { JobResolveBudget = TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, new Catalogue(), [new BookAndGame()], scanning, NullLogger<TextSearchResolver>.Instance);
        var grounder = new EvalGrounder(
            new IdentifyGrounding(resolver, new FixedScoring()), null!, scanning,
            Options.Create(new EvalsOptions { GroundingParallelism = 4 }), new NoSerials(), TimeProvider.System);
        return new EvalRunScoring(null!, null!, grounder, Options.Create(new EvalsOptions()), new FixedScoring(), NullLogger<EvalRunScoring>.Instance);
    }

    private static EvalReadItemInput Read(string title, params string[] lines) => new(
        new IdentifyItemInput(null, null, title, title, null, null, null, null, null, null, null, null, Lines: lines.Length > 0 ? lines : null),
        null);

    private static EvalPhotoInput Photo(ScanEngine engine, params EvalReadItemInput[] items) =>
        new("p1", engine, EvalOutcome.Ok, null, items, 0, 1000, null, ThermalState.Nominal);

    [Fact]
    public async Task The_model_rows_read_is_told_the_platform_printed_on_a_line_of_the_vision_row()
    {
        var vision = Photo(ScanEngine.VisionPipeline, Read("Lantern Row", "LANTERN ROW", "PlayStation.2"));
        var model = Photo(ScanEngine.AppleModel, Read(Title));

        var grounded = await Scoring().GroundPhotoAsync([vision, model], new EvalGroundingMemo(), CancellationToken.None);

        var apple = grounded.Single(g => g.Engine == "apple_model");
        apple.Items.Single().Candidates[0].Platform.ShouldBe("PS2");
    }

    [Fact]
    public async Task The_vision_rows_read_is_told_the_platform_printed_on_a_line_of_the_model_row()
    {
        var vision = Photo(ScanEngine.VisionPipeline, Read(Title));
        var model = Photo(ScanEngine.AppleModel, Read("Lantern Row", "PlayStation.2"));

        var grounded = await Scoring().GroundPhotoAsync([vision, model], new EvalGroundingMemo(), CancellationToken.None);

        grounded.Single(g => g.Engine == "vision_pipeline").Items.Single(i => i.ReadTitle == Title).Candidates[0].Platform.ShouldBe("PS2");
    }

    [Fact]
    public async Task With_no_console_printed_on_any_row_the_providers_order_stands()
    {
        var vision = Photo(ScanEngine.VisionPipeline, Read("Lantern Row", "LANTERN ROW"));
        var model = Photo(ScanEngine.AppleModel, Read(Title));

        var grounded = await Scoring().GroundPhotoAsync([vision, model], new EvalGroundingMemo(), CancellationToken.None);

        grounded.Single(g => g.Engine == "apple_model").Items.Single().Candidates[0].Platform.ShouldBeNull();
    }
}
