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

// The leading-word rule of the span retry is for the server's own reads only, and the mark that says so travels from
// ServerReadInput.ReadOf through the cleaning to the grounding. The same words read by the phone's model (no text, no lines, as an
// eval model item is) keep the plain rule. Film titles are the public ones of #815, the fillers neutral.
public class EvalServerReadLeadingWordTests
{
    private const string Read = "HIGHLANDER THE FINAL DIMENSION";

    private static readonly string[] Fillers = ["Quiet Harbor Atlas", "Lantern Row", "Copper Gate"];

    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    /// <summary>Answers only the span without the franchise word, with the wrong film.</summary>
    private sealed class FinalFilms : ICatalogProvider
    {
        public string Name => "tmdb";

        public bool Supports(WorkKind kind) => kind == WorkKind.Film;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderRelease>>(
                string.Equals(query.Title, "THE FINAL DIMENSION", StringComparison.OrdinalIgnoreCase)
                    ? [new ProviderRelease { Provider = "tmdb", ExternalId = "movie/1", WorkKey = "movie/1", Title = "The Final Destination", Kind = WorkKind.Film, Year = 2009 }]
                    : []);
    }

    private sealed class Reader(params ShelfReadItem[] items) : IServerReadings
    {
        public bool IsConfigured => true;

        public string ReaderLabel => "model example, prompt hash 000000000000";

        public Task<ServerReading?> ReadAsync(string photoId, bool cachedOnly, CancellationToken cancellationToken) =>
            Task.FromResult<ServerReading?>(new ServerReading(new ShelfReadResult(items, ShelfReadStatus.Ok), 10));
    }

    private static EvalRunScoring Scoring(IServerReadings server)
    {
        var scanning = new ScanningOptions { JobResolveBudget = TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, new Catalogue(), [new FinalFilms()], scanning, NullLogger<TextSearchResolver>.Instance);
        var grounder = new EvalGrounder(
            new IdentifyGrounding(resolver, new FixedScoring()), null!, scanning,
            Options.Create(new EvalsOptions { GroundingParallelism = 4 }), new NoSerials(), TimeProvider.System);
        return new EvalRunScoring(null!, null!, grounder, Options.Create(new EvalsOptions()), new FixedScoring(), NullLogger<EvalRunScoring>.Instance, server);
    }

    private static EvalReadItemInput ModelItem(string title) => new(
        new IdentifyItemInput(null, null, null, title, null, null, null, WorkKind.Film, null, null, null, null), ResolvedStep.Model, ScanTier.T1);

    private static string? BestOf(GroundedPhoto row) =>
        row.Items.SingleOrDefault(item => item.ReadTitle == Read)?.Candidates is { Count: > 0 } candidates ? candidates[0].Title : null;

    [Fact]
    public void A_server_read_is_marked_as_one_and_a_phone_read_is_not()
    {
        ServerReadInput.ReadOf(new ShelfReadItem(Read, "", ShelfItemKind.Film)).ServerRead.ShouldBeTrue();
        ModelItem(Read).Read.ServerRead.ShouldBeFalse();
    }

    [Fact]
    public async Task The_server_row_gets_the_leading_word_rule_and_the_phone_model_row_with_the_same_words_does_not()
    {
        var titles = new[] { Read }.Concat(Fillers).ToArray();
        var phone = new EvalPhotoInput(
            "p1", ScanEngine.AppleModel, EvalOutcome.Ok, null, [.. titles.Select(ModelItem)], 0, 1000, null, ThermalState.Nominal);
        var server = new Reader([.. titles.Select(title => new ShelfReadItem(title, "", ShelfItemKind.Film))]);

        var rows = await Scoring(server).GroundPhotoAsync([phone], new EvalGroundingMemo(), CancellationToken.None, ServerReadMode.Read);

        BestOf(rows.Single(r => r.Engine == "apple_model")).ShouldBe("The Final Destination");
        BestOf(rows.Single(r => r.Engine == ServerEngines.Model)).ShouldBeNull();
    }
}
