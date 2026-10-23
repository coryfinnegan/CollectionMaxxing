using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.RateLimiting;

// LOCKED acceptance tests of AUTH-1 (issue #228), lockout: the per-client limits on /setup, /account/recover and /account/login never
// stop the owner from signing in. Do not edit, weaken, skip or delete: add new tests instead.
public class SignInLockoutTests(ApiFixture api) : RateLimitTestBase(api)
{
    [Fact]
    public async Task Auth0_callbacks_are_never_counted()
    {
        var client = From("10.0.0.1", "203.0.113.5");

        for (var i = 0; i < 8; i++)
        {
            var response = await client.GetAsync($"/signin-oidc?code=x{i}&state=y{i}");
            response.StatusCode.ShouldBe(HttpStatusCode.Found, $"callback {i}");
        }

        // ...and they used up nothing: this client can still start three sign-ins.
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/account/login")).StatusCode.ShouldBe(HttpStatusCode.Found);
        }
    }

    [Fact]
    public async Task A_stranger_hammering_every_sign_in_endpoint_does_not_stop_the_owner_from_signing_in()
    {
        var (_, owner) = await SetUpOwnerAsync(Browser("10.0.0.1", "203.0.113.6"));
        var stranger = From("10.0.0.1", "203.0.113.5");
        for (var i = 0; i < 5; i++)
        {
            await stranger.PostAsJsonAsync("/setup", new { code = "WRONG-" + i });
            await stranger.PostAsJsonAsync("/account/recover", new { code = "WRONG-" + i });
            await stranger.GetAsync("/account/login");
        }

        (await stranger.GetAsync("/account/login")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the stranger is limited...");

        var ownerAgain = Browser("10.0.0.1", "203.0.113.7", await IdentityOfAsync(owner.UserId));
        var signedIn = await ownerAgain.LoginAsync();
        signedIn.Headers.Location?.ToString().ShouldBe("/", "...and the owner is not");
        ownerAgain.HasSessionCookie.ShouldBeTrue();
    }

    [Fact]
    public async Task A_recovery_code_still_works_for_the_owner_while_a_stranger_guesses_codes()
    {
        var (_, owner) = await SetUpOwnerAsync(Browser("10.0.0.1", "203.0.113.6"));
        var stranger = From("10.0.0.1", "203.0.113.5");
        for (var i = 0; i < 5; i++)
        {
            await stranger.PostAsJsonAsync("/account/recover", new { code = "WRONG-" + i });
        }

        var recovering = Browser("10.0.0.1", "203.0.113.8", TestIdentity.Apple("Owner"));
        var done = await recovering.ContinueAsync(await recovering.StartRecoveryAsync(await App.IssueCodeAsync(Domain.Accounts.SetupCodePurpose.Recovery)));

        done.Headers.Location?.ToString().ShouldBe("/");
        (await recovering.MeAsync()).UserId.ShouldBe(owner.UserId);
    }

    private async Task<TestIdentity> IdentityOfAsync(Guid userId)
    {
        await using var context = Db.CreateContext();
        return new TestIdentity(context.Logins.Where(l => l.UserId == userId).Select(l => l.Subject).Single());
    }
}
