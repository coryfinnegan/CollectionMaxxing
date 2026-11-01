using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// Once <see cref="EvalRunScoring.ScoreAsync"/> starts writing the result files, a token that asks the run to stop (the API shutting down,
/// the phone stopping the run) must never cut the write short: a half-written set of files would look complete on the next read. The class
/// guards this by writing with <see cref="CancellationToken.None"/>, never the caller's token (#606).
/// </summary>
public sealed class ScoreAsyncWritingTests
{
    private sealed class SpyResults : IEvalResults
    {
        public CancellationToken? SeenInSaveAsync { get; private set; }

        public bool IsConfigured => true;

        public Task SaveAsync(EvalSummary summary, GroundedRun run, string reportMarkdown, CancellationToken cancellationToken)
        {
            SeenInSaveAsync = cancellationToken;
            return Task.CompletedTask;
        }

        public EvalSummary? LoadLatestSummary() => null;

        public GroundedRun? LoadLatestRun() => null;

        public IReadOnlyDictionary<string, double>? LoadBaseline() => null;

        public EvalBaselineDoc? LoadBaselineDoc() => null;

        public bool TryWriteDraft(string id, string json) => false;
    }

    /// <summary>Never legitimately called in this test: every photo already has a truth, so no draft is written through the golden folder.</summary>
    private sealed class UnusedGoldenSet : IGoldenSet
    {
        public bool IsAvailable => throw new NotSupportedException();

        public IReadOnlyList<GoldenPhotoDto> List() => throw new NotSupportedException();

        public GoldenImage? OpenImage(string id) => throw new NotSupportedException();

        public string? SetOf(string id) => throw new NotSupportedException();

        public string? ReadTruth(string id) => throw new NotSupportedException();

        public bool HasDraft(string id) => throw new NotSupportedException();

        public GoldenAddResult Add(string goldenSet, string id, GoldenPhotoFormat format, ReadOnlyMemory<byte> content) => throw new NotSupportedException();

        public bool TryWriteDraft(string id, string json) => throw new NotSupportedException();
    }

    private sealed class FixedScoring(ScoringOptions value) : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue => value;

        public ScoringOptions Get(string? name) => value;

        public IDisposable OnChange(Action<ScoringOptions, string?> listener) => new Nop();

        private sealed class Nop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public async Task Writes_the_result_files_with_CancellationToken_None_so_a_token_that_asks_to_stop_never_cuts_them_short()
    {
        var golden = new SyntheticGolden();
        golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64"));
        var grounded = RunOf(Photo("c1", Apple, [Matched("Star Fox 64")]));
        var run = new EvalRunToScore(grounded.RunId, grounded.At, grounded.Device, grounded.ConfigVersion, grounded.Partial, grounded.BaselineNote, grounded.Photos);
        var truthSet = new EvalTruthSet(new Dictionary<string, GoldenPhotoDto>(), TruthsOf(golden), []);
        var results = new SpyResults();
        var scoring = new EvalRunScoring(
            new UnusedGoldenSet(), results, null!, Options.Create(new EvalsOptions()), new FixedScoring(new ScoringOptions()), NullLogger<EvalRunScoring>.Instance);

        // A token the caller could still cancel after this returns: standing in for the API's shutdown token, or the phone's stop request.
        using var stopping = new CancellationTokenSource();

        var scored = await scoring.ScoreAsync(run, truthSet, [], stopping.Token);

        scored.IsSuccess.ShouldBeTrue();
        results.SeenInSaveAsync.ShouldBe(CancellationToken.None);
    }
}
