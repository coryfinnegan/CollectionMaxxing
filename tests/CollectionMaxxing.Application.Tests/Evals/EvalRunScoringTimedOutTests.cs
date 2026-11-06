using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>Grounding keeps the phone's <c>timedOut</c> flag on the photo only when it is true, so the scorer and the kept readings never see a false one.</summary>
public class EvalRunScoringTimedOutTests
{
    private sealed class FixedScoring : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue { get; } = new();

        public ScoringOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ScoringOptions, string?> listener) => null;
    }

    private static EvalRunScoring Scoring() => new(
        null!, null!, new EvalGrounder(null!, null!, new ScanningOptions(), Options.Create(new EvalsOptions()), null!, TimeProvider.System),
        Options.Create(new EvalsOptions()), new FixedScoring(), NullLogger<EvalRunScoring>.Instance);

    private static EvalPhotoInput Photo(bool? timedOut) =>
        new("c1", ScanEngine.AppleModel, EvalOutcome.Ok, null, [], 0, 9000, null, ThermalState.Nominal, TimedOut: timedOut);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public async Task A_grounded_photo_keeps_timedOut_only_when_it_is_true(bool? sent, bool? kept)
    {
        var grounded = await Scoring().GroundPhotoAsync([Photo(sent)], new EvalGroundingMemo(), CancellationToken.None);

        grounded.ShouldHaveSingleItem().TimedOut.ShouldBe(kept);
    }
}
