using CollectionMaxxing.Application.Accounts.Devices;

namespace CollectionMaxxing.Application.Tests.Accounts;

public class PairingSecretsTests
{
    [Theory]
    [InlineData("123456", "123456")]
    [InlineData("123 456", "123456")]
    [InlineData("123-456", "123456")]
    [InlineData("000123", "000123")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12345a", null)]
    [InlineData("abcdef", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_typed_code_is_accepted_only_as_exactly_six_digits(string? typed, string? expected)
    {
        PairingSecrets.NormalizeCode(typed).ShouldBe(expected);
    }

    [Fact]
    public void A_new_code_is_six_digits_and_a_new_token_is_long_and_different_each_time()
    {
        PairingSecrets.NormalizeCode(PairingSecrets.NewCode()).ShouldNotBeNull();
        PairingSecrets.NewToken().ShouldNotBe(PairingSecrets.NewToken());
        PairingSecrets.NewToken().Length.ShouldBeGreaterThan(40);
        PairingSecrets.Hash("a").ShouldBe(PairingSecrets.Hash("a"));
        PairingSecrets.Hash("a").ShouldNotBe(PairingSecrets.Hash("b"));
    }
}
