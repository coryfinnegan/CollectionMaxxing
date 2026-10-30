using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>One eval run's memory of what grounding answered: the same cleaned read is asked once.</summary>
public class EvalGroundingMemoTests
{
    private static ReadFacts Facts(string title = "Lantern Keep", int? year = null, string? platform = "Nintendo 64") =>
        new(title, title, null, null, ItemCompleteness.CartOnly, WorkKind.Game, ReleaseFormat.GameCartridge, year, platform);

    private static BarcodeResolution Answer(ResolutionVerdict verdict = ResolutionVerdict.Matched) => new(verdict, [], null, []);

    private sealed class Asker
    {
        public int Calls { get; private set; }

        public Task<BarcodeResolution> Ask(BarcodeResolution answer)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task The_same_read_asked_twice_is_grounded_once_and_both_get_the_same_answer()
    {
        var memo = new EvalGroundingMemo();
        var asker = new Asker();
        var answer = Answer();

        // Two separately built reads with the same fields: the key is the value, not the instance.
        var first = await memo.GroundAsync(Facts(), ScoringProfileKind.LooseCartridge, () => asker.Ask(answer));
        var second = await memo.GroundAsync(Facts(), ScoringProfileKind.LooseCartridge, () => asker.Ask(Answer(ResolutionVerdict.NotFound)));

        asker.Calls.ShouldBe(1);
        second.ShouldBeSameAs(first);
        first.ShouldBeSameAs(answer);
        memo.Grounded.ShouldBe(1);
        memo.Reused.ShouldBe(1);
    }

    [Fact]
    public async Task The_same_words_read_under_another_profile_are_grounded_again()
    {
        var memo = new EvalGroundingMemo();
        var asker = new Asker();

        await memo.GroundAsync(Facts(), ScoringProfileKind.LooseCartridge, () => asker.Ask(Answer()));
        await memo.GroundAsync(Facts(), ScoringProfileKind.ShelfSpine, () => asker.Ask(Answer()));

        asker.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData("Lantern Keep II", null, "Nintendo 64")]
    [InlineData("Lantern Keep", 1997, "Nintendo 64")]
    [InlineData("Lantern Keep", null, "Super Nintendo")]
    [InlineData("Lantern Keep", null, null)]
    public async Task A_read_that_differs_in_any_field_is_grounded_again(string title, int? year, string? platform)
    {
        var memo = new EvalGroundingMemo();
        var asker = new Asker();

        await memo.GroundAsync(Facts(), ScoringProfileKind.LooseCartridge, () => asker.Ask(Answer()));
        await memo.GroundAsync(Facts(title, year, platform), ScoringProfileKind.LooseCartridge, () => asker.Ask(Answer()));

        asker.Calls.ShouldBe(2);
        memo.Reused.ShouldBe(0);
    }

    [Fact]
    public async Task A_grounding_that_failed_is_not_remembered()
    {
        var memo = new EvalGroundingMemo();
        var asker = new Asker();

        await Should.ThrowAsync<InvalidOperationException>(
            () => memo.GroundAsync(Facts(), ScoringProfileKind.LooseCartridge, () => throw new InvalidOperationException("provider down")));
        var answer = await memo.GroundAsync(Facts(), ScoringProfileKind.LooseCartridge, () => asker.Ask(Answer()));

        asker.Calls.ShouldBe(1);
        answer.Verdict.ShouldBe(ResolutionVerdict.Matched);
    }
}
