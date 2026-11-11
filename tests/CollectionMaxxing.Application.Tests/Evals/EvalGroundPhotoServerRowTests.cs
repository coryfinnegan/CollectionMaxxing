using System.Text.Json;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Evals;

// The server reads a golden photo itself (ADR-033) and the reading becomes a server_model row, grounded by the call the phone's model items go through.
// Nothing the phone's rows are made of may change for it. Neutral titles throughout.
public class EvalGroundPhotoServerRowTests
{
    private const string Title = "Quiet Harbor Atlas";
    private const string Creator = "A. Example";

    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    /// <summary>A provider that knows the title as a book and as a PS2 game, the book first.</summary>
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

    private sealed class FakeServerReadings(ServerReading? reading, bool configured = true) : IServerReadings
    {
        public int Calls { get; private set; }

        public bool IsConfigured => configured;

        public string ReaderLabel => "model example, prompt hash 000000000000";

        public Task<ServerReading?> ReadAsync(string photoId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(reading);
        }
    }

    private static EvalRunScoring Scoring(IServerReadings? server)
    {
        var scanning = new ScanningOptions { JobResolveBudget = TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, new Catalogue(), [new BookAndGame()], scanning, NullLogger<TextSearchResolver>.Instance);
        var grounder = new EvalGrounder(
            new IdentifyGrounding(resolver, new FixedScoring()), null!, scanning,
            Options.Create(new EvalsOptions { GroundingParallelism = 4 }), new NoSerials(), TimeProvider.System);
        return new EvalRunScoring(null!, null!, grounder, Options.Create(new EvalsOptions()), new FixedScoring(), NullLogger<EvalRunScoring>.Instance, server);
    }

    private static EvalReadItemInput ModelItem(string title, string? creator, WorkKind? kind) => new(
        new IdentifyItemInput(null, null, null, title, creator, null, null, kind, null, null, null, null), ResolvedStep.Model, ScanTier.T1);

    private static EvalPhotoInput Photo(ScanEngine engine, WorkKind? printKind = null, params EvalReadItemInput[] items) =>
        new("p1", engine, EvalOutcome.Ok, null, items, 0, 1000, null, ThermalState.Nominal, PrintKind: printKind);

    private static ServerReading Read(ShelfReadStatus status = ShelfReadStatus.Ok, params ShelfReadItem[] items) => new(new ShelfReadResult(items, status), 12_345);

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static readonly EvalPhotoInput PhoneModel = Photo(ScanEngine.AppleModel, null, ModelItem("Lantern Row", null, WorkKind.Book));
    private static readonly EvalPhotoInput PhoneVision = Photo(ScanEngine.VisionPipeline, null, ModelItem("Lantern Row", null, null));

    private static Task<IReadOnlyList<GroundedPhoto>> Ground(EvalRunScoring scoring, bool readOnServer, params EvalPhotoInput[] phone) =>
        scoring.GroundPhotoAsync(phone, new EvalGroundingMemo(), CancellationToken.None, readOnServer);

    [Fact]
    public async Task With_a_reader_the_photo_gets_a_server_model_row_after_the_phones_rows_and_without_one_it_does_not()
    {
        var server = new FakeServerReadings(Read(items: new ShelfReadItem(Title, Creator, ShelfItemKind.Book)));

        var with = await Ground(Scoring(server), true, PhoneModel, PhoneVision);
        var without = await Ground(Scoring(null), true, PhoneModel, PhoneVision);

        with.Select(g => g.Engine).ShouldBe(["apple_model", "vision_pipeline", "server_model"]);
        without.Select(g => g.Engine).ShouldBe(["apple_model", "vision_pipeline"]);
        with[2].Items.Single().ReadTitle.ShouldBe(Title);
        with[2].LatencyMs.ShouldBe(12_345, "the reader's own time");
        with[2].CapturedCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_reader_that_is_not_set_up_or_a_photo_it_cannot_open_gives_no_row_and_the_one_shot_route_never_asks()
    {
        var item = new ShelfReadItem(Title, "", ShelfItemKind.Book);
        var off = new FakeServerReadings(Read(items: item), configured: false);
        var noPicture = new FakeServerReadings(null);
        var unasked = new FakeServerReadings(Read(items: item));

        (await Ground(Scoring(off), true, PhoneModel)).ShouldHaveSingleItem();
        (await Ground(Scoring(noPicture), true, PhoneModel)).ShouldHaveSingleItem();
        (await Ground(Scoring(unasked), false, PhoneModel)).ShouldHaveSingleItem();

        off.Calls.ShouldBe(0);
        unasked.Calls.ShouldBe(0, "a request never waits for a reader");
    }

    [Fact]
    public async Task Every_row_the_phone_made_is_identical_with_and_without_the_reader()
    {
        var server = new FakeServerReadings(Read(items: [new ShelfReadItem(Title, Creator, ShelfItemKind.Game), new ShelfReadItem("Lantern Row", "", ShelfItemKind.Book)]));
        var vision = Photo(ScanEngine.VisionPipeline, WorkKind.Game, ModelItem("Lantern Row", null, null));
        var model = Photo(ScanEngine.AppleModel, null, ModelItem("Lantern Row", null, WorkKind.Book));

        var with = await Ground(Scoring(server), true, vision, model);
        var without = await Ground(Scoring(null), true, vision, model);

        Json(with.Where(g => g.Engine != ServerEngines.Model)).ShouldBe(Json(without));
    }

    [Fact]
    public async Task A_reader_item_grounds_exactly_as_a_model_item_with_the_same_title_creator_and_kind()
    {
        var server = new FakeServerReadings(Read(items: new ShelfReadItem(Title, Creator, ShelfItemKind.Book)));
        var asTheModel = Photo(ScanEngine.AppleModel, null, ModelItem(Title, Creator, WorkKind.Book));

        var grounded = await Ground(Scoring(server), true, PhoneVision);
        var viaModel = (await Ground(Scoring(null), true, asTheModel)).Single();

        Json(grounded.Single(g => g.Engine == ServerEngines.Model).Items).ShouldBe(Json(viaModel.Items));
    }

    [Fact]
    public async Task The_server_row_is_grounded_again_with_the_printed_kind_lent_as_the_model_row_is()
    {
        var server = new FakeServerReadings(Read(items: new ShelfReadItem(Title, "", ShelfItemKind.None)));
        var visionNamingTheKind = Photo(ScanEngine.VisionPipeline, WorkKind.Game, ModelItem("Lantern Row", null, null));
        var asTheModel = Photo(ScanEngine.AppleModel, null, ModelItem(Title, null, null));

        var grounded = await Ground(Scoring(server), true, visionNamingTheKind);
        var viaModel = (await Ground(Scoring(null), true, visionNamingTheKind, asTheModel)).Single(g => g.Engine == "apple_model");

        var row = grounded.Single(g => g.Engine == ServerEngines.Model);
        row.ItemsLentPrint.ShouldNotBeNull();
        Json(row.ItemsLentPrint).ShouldBe(Json(viaModel.ItemsLentPrint));
        row.ItemsLentPrint![0].Candidates[0].Kind.ShouldBe("game");
        Json(row.Items).ShouldBe(Json(viaModel.Items), "the row's own items get no lent grounding");
    }

    [Fact]
    public async Task A_failed_read_is_an_error_row_with_no_items()
    {
        var server = new FakeServerReadings(Read(ShelfReadStatus.Unavailable));

        var row = (await Ground(Scoring(server), true, PhoneModel)).Single(g => g.Engine == ServerEngines.Model);

        row.Outcome.ShouldBe("error");
        row.Items.ShouldBeEmpty();
        row.TimedOut.ShouldBeNull();
    }

    [Fact]
    public async Task A_read_cut_short_is_a_timeout_with_its_items_kept()
    {
        var server = new FakeServerReadings(Read(ShelfReadStatus.CutShort, new ShelfReadItem(Title, "", ShelfItemKind.Book)));

        var row = (await Ground(Scoring(server), true, PhoneModel)).Single(g => g.Engine == ServerEngines.Model);

        row.Outcome.ShouldBe("ok");
        row.TimedOut.ShouldBe(true);
        row.Items.Single().ReadTitle.ShouldBe(Title);
    }

    [Fact]
    public void A_reader_item_claims_T1_and_carries_only_a_title_a_creator_and_a_kind()
    {
        var input = ServerReadInput.Of("p1", Read(items: [new ShelfReadItem(Title, Creator, ShelfItemKind.Tv), new ShelfReadItem("Lantern Row", "", ShelfItemKind.None)]));

        input.Items!.ShouldAllBe(i => i.Tier == ScanTier.T1 && i.Read.Box == null && i.Read.Format == null && i.Read.Lines == null && i.Read.Year == null);
        input.Items![0].Read.Kind.ShouldBe(WorkKind.Tv);
        input.Items![0].Read.Creator.ShouldBe(Creator);
        input.Items![1].Read.Kind.ShouldBeNull();
        input.Items![1].Read.Creator.ShouldBeNull();
    }
}
