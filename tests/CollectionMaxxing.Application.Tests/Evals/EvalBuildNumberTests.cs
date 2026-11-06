using CollectionMaxxing.Application.Evals;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>
/// The TestFlight build number a phone reports with its run: one to three dot-separated integers, at most 20 characters (the rule in
/// <c>app.config.ts</c>). Anything else is dropped; it is never a reason to refuse the run.
/// </summary>
public class EvalBuildNumberTests
{
    [Theory]
    [InlineData("150", "150")]
    [InlineData("1.2", "1.2")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  148  ", "148")]
    [InlineData("12345678901234567890", "12345678901234567890")]
    public void A_build_number_of_one_to_three_dot_separated_integers_is_kept(string given, string kept) =>
        EvalBuildNumber.Clean(given).ShouldBe(kept);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("1..2")]
    [InlineData(".1")]
    [InlineData("1.")]
    [InlineData("-1")]
    [InlineData("1 2")]
    [InlineData("1e3")]
    [InlineData("123456789012345678901")]
    [InlineData("٣")]
    [InlineData("1\n2")]
    public void Anything_else_is_dropped(string? given) => EvalBuildNumber.Clean(given).ShouldBeNull();
}
