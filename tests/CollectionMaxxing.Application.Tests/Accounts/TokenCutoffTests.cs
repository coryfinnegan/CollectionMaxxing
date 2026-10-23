using CollectionMaxxing.Application.Accounts.IdentityProvider;

namespace CollectionMaxxing.Application.Tests.Accounts;

public class TokenCutoffTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 23, 12, 0, 5, 900, TimeSpan.Zero);
    private static readonly long CutoffSecond = new DateTimeOffset(2026, 10, 23, 12, 0, 5, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void Nobody_is_refused_while_there_is_no_cutoff() => TokenCutoff.Refuses(null, 1).ShouldBeFalse();

    [Fact]
    public void A_token_issued_a_second_before_the_cutoffs_second_is_refused() => TokenCutoff.Refuses(Cutoff, CutoffSecond - 1).ShouldBeTrue();

    [Fact]
    public void A_token_issued_in_the_cutoffs_own_second_is_a_new_sign_in_and_works() => TokenCutoff.Refuses(Cutoff, CutoffSecond).ShouldBeFalse();

    [Fact]
    public void A_token_issued_later_works() => TokenCutoff.Refuses(Cutoff, CutoffSecond + 60).ShouldBeFalse();

    [Fact]
    public void A_token_that_does_not_say_when_it_was_issued_is_refused_once_there_is_a_cutoff() => TokenCutoff.Refuses(Cutoff, null).ShouldBeTrue();
}
