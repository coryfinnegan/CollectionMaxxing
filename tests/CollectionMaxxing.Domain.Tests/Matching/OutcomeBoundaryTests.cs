using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Locked acceptance tests for CAT-1 (docs/02 section 6, outcome table). Do not weaken, skip or delete.
// The thresholds are score >= 0.90 AND lead >= 0.08 for Matched, score >= 0.60 for Ambiguous.
public class OutcomeBoundaryTests
{
    private static readonly ScoringThresholds Defaults = new();

    // ---- score: 0.90 / 0.8999 -----------------------------------------------------------------

    [Fact]
    public void A_score_of_exactly_0_90_with_a_clear_lead_is_Matched()
    {
        MatchScorer.Classify(0.90, 0.50, Defaults).ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_score_of_0_8999_is_not_Matched_however_big_the_lead()
    {
        MatchScorer.Classify(0.8999, 0.10, Defaults).ShouldBe(MatchOutcome.Ambiguous);
        MatchScorer.Classify(0.8999, null, Defaults).ShouldBe(MatchOutcome.Ambiguous);
    }

    // ---- lead: 0.08 / 0.0799 ------------------------------------------------------------------

    [Fact]
    public void A_lead_of_exactly_0_08_is_Matched()
    {
        MatchScorer.Classify(0.95, 0.87, Defaults).ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_lead_of_0_0799_is_Ambiguous()
    {
        MatchScorer.Classify(0.95, 0.8701, Defaults).ShouldBe(MatchOutcome.Ambiguous);
    }

    // Subtraction in binary floating point gets these wrong (1.0 - 0.92 is 0.07999999999999996 and 0.9 - 0.82 is
    // 0.08000000000000007), so the rule must not depend on it.
    [Theory]
    [InlineData(1.00, 0.92)]
    [InlineData(0.90, 0.82)]
    [InlineData(0.98, 0.90)]
    [InlineData(0.95, 0.87)]
    [InlineData(0.93, 0.85)]
    public void A_lead_of_exactly_0_08_is_Matched_whatever_the_floating_point_noise(double best, double second)
    {
        MatchScorer.Classify(best, second, Defaults).ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void With_no_runner_up_the_lead_is_not_a_constraint()
    {
        MatchScorer.Classify(0.90, null, Defaults).ShouldBe(MatchOutcome.Matched);
        MatchScorer.Classify(1.00, null, Defaults).ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_tie_at_the_top_is_Ambiguous()
    {
        MatchScorer.Classify(1.0, 1.0, Defaults).ShouldBe(MatchOutcome.Ambiguous);
    }

    // ---- ambiguous: 0.60 / 0.5999 -------------------------------------------------------------

    [Fact]
    public void A_score_of_exactly_0_60_is_Ambiguous()
    {
        MatchScorer.Classify(0.60, null, Defaults).ShouldBe(MatchOutcome.Ambiguous);
        MatchScorer.Classify(0.60, 0.59, Defaults).ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void A_score_of_0_5999_is_NotFound()
    {
        MatchScorer.Classify(0.5999, null, Defaults).ShouldBe(MatchOutcome.NotFound);
        MatchScorer.Classify(0.5999, 0.10, Defaults).ShouldBe(MatchOutcome.NotFound);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(0.5)]
    public void Low_scores_are_NotFound(double best)
    {
        MatchScorer.Classify(best, null, Defaults).ShouldBe(MatchOutcome.NotFound);
    }

    // ---- the boundaries move with the configuration --------------------------------------------

    [Fact]
    public void The_thresholds_are_read_from_the_configuration_not_hard_coded()
    {
        var custom = new ScoringThresholds { MatchedScore = 0.80, MatchedLead = 0.20, AmbiguousScore = 0.30 };

        MatchScorer.Classify(0.80, 0.60, custom).ShouldBe(MatchOutcome.Matched);
        MatchScorer.Classify(0.7999, 0.10, custom).ShouldBe(MatchOutcome.Ambiguous);
        MatchScorer.Classify(0.85, 0.66, custom).ShouldBe(MatchOutcome.Ambiguous);   // lead 0.19
        MatchScorer.Classify(0.30, null, custom).ShouldBe(MatchOutcome.Ambiguous);
        MatchScorer.Classify(0.2999, null, custom).ShouldBe(MatchOutcome.NotFound);
    }
}
