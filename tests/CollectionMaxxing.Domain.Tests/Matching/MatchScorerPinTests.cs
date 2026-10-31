namespace CollectionMaxxing.Domain.Tests.Matching;

// How a read scores against its candidates must not change when the scorer is made faster: every score, term and outcome
// is pinned to the exact double for reads and candidate lists taken from the matching tests (see MatchScorerPinScenarios).
public class MatchScorerPinTests
{
    public static TheoryData<string> ScenarioNames => new(MatchScorerPinScenarios.Names);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void A_scenario_scores_exactly_as_pinned(string name)
    {
        MatchScorerPinScenarios.Describe(MatchScorerPinScenarios.Named(name)).ShouldBe(MatchScorerPinExpected.ByScenario[name]);
    }

    [Fact]
    public void Every_scenario_has_a_pinned_score_and_every_pin_has_a_scenario()
    {
        MatchScorerPinExpected.ByScenario.Keys.ShouldBe(MatchScorerPinScenarios.Names, ignoreOrder: true);
    }
}
