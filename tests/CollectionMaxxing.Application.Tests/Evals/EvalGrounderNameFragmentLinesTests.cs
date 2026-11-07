using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Evals;

// A Vision read that is an author's name (its best release is titled with that name) is skipped as a fragment, unless another line of
// its spine finds the book. The creator swap finds something weaker first; the lines are still asked. Neutral names throughout.
public class EvalGrounderNameFragmentLinesTests
{
    private sealed class NoSerials : IGameSerialLookup
    {
        public IReadOnlyList<GameSerialMatch> Find(string? code, string? platform = null) => [];
    }

    private static EvalReadItemInput Spine(string title, string? creator = null, string[]? lines = null) => new(
        new IdentifyItemInput(null, null, title, title, creator, null, null, null, null, null, null, null, Lines: lines),
        null);

    private static Task<GroundedShot> GroundAsync(Providers providers, params EvalReadItemInput[] reads)
    {
        var scanning = new ScanningOptions { JobResolveBudget = TimeSpan.FromSeconds(30) };
        var resolver = new TextSearchResolver(null!, new Catalogue(), [providers], scanning, NullLogger<TextSearchResolver>.Instance);
        var grounder = new EvalGrounder(
            new IdentifyGrounding(resolver, new FixedScoring()), null!, scanning,
            Options.Create(new EvalsOptions { GroundingParallelism = 4 }), new NoSerials(), TimeProvider.System);
        return grounder.GroundWithSkipsAsync(ScanEngine.VisionPipeline, reads, new EvalGroundingMemo(), null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_name_fragment_vision_read_with_lines_is_no_longer_skipped_and_is_marked_found_by_line()
    {
        var providers = new Providers(false,
            Known("Tess Harrowgate", "n1") with { Creators = ["Tess Harrowgate"] },
            Known("Tess Harrowgate", "n2") with { Creators = ["Tess Harrowgate"], Year = 2015 },
            Known("Quiet Harbor", "s1") with { Creators = ["Mara Quillon"] },
            Known("Copper Lantern", "t1") with { Creators = ["Tess Harrowgate"] },
            Known("Alpha Quest", "a1") with { Creators = ["Ada Brook"] },
            Known("Beta Quest", "a2") with { Creators = ["Ada Brook"] },
            Known("Gamma Quest", "a3") with { Creators = ["Ada Brook"] });

        var shot = await GroundAsync(
            providers,
            Spine("Alpha Quest", "Ada Brook"),
            Spine("Beta Quest", "Ada Brook"),
            Spine("TESS HARROWGATE", "Quiet Harbor", ["TESS HARROWGATE", "COPPER LANTERN"]),
            Spine("Gamma Quest", "Ada Brook"));

        var found = shot.Items.Where(i => i.FoundByLine).ShouldHaveSingleItem();
        found.Verdict.ShouldBe("ambiguous");
        found.Candidates.Select(c => c.Title).ShouldContain("Copper Lantern");
        (shot.Skipped ?? []).ShouldNotContain(i => i.ReadTitle == "TESS HARROWGATE");
    }
}
