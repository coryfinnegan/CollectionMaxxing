using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// The passkey ceremonies are this server's answer to <c>state</c>, <c>nonce</c> and PKCE: the server seals what it expects (the challenge, what
/// the ceremony is for) in the <c>__Host-cm-ceremony</c> cookie and the browser's answer must match. Tampered, missing, swapped, stale and
/// replayed ceremonies must all fail, and fail the same way for the same reason (401 or 4xx, never a session).
/// </summary>
public class PasskeyCeremonyAbuseTests(ApiFixture api) : AuthTest(api)
{
    private const string Ceremony = CeremonyCookie.Name;

    private static string CookieValue(WebClient browser, string name) =>
        browser.SetCookies.Last(c => c.Name.Value == name && !string.IsNullOrEmpty(c.Value.Value)).Value.Value!;

    /// <summary>Asks for sign-in options and signs them, but does not finish. Returns the signed assertion and the ceremony cookie that goes with it.</summary>
    private static async Task<(JsonElement Credential, string CeremonyCookie)> PrepareLoginAsync(WebClient browser)
    {
        var response = await browser.PostAsync("/account/passkey/request-options");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var options = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options");
        var credential = JsonDocument.Parse(browser.Authenticator.Sign(options.GetRawText())).RootElement.Clone();
        return (credential, CookieValue(browser, Ceremony));
    }

    private static Task<HttpResponseMessage> LoginWithAsync(WebClient browser, JsonElement credential, string? ceremony) =>
        browser.SendAsync(HttpMethod.Post, "/account/login", new { credential }, tweak: request =>
        {
            if (ceremony is not null)
            {
                request.Headers.Add("Cookie", $"{Ceremony}={ceremony}");
            }
        });

    private async Task<WebClient> VictimAsync()
    {
        var (owner, _) = await SetUpOwnerAsync();
        var victim = App.NewBrowser();
        victim.Authenticator = owner.Authenticator;
        return victim;
    }

    [Fact]
    public async Task A_login_with_no_ceremony_at_all_never_signs_anyone_in()
    {
        var victim = await VictimAsync();
        var (credential, _) = await PrepareLoginAsync(victim);
        var attacker = App.NewBrowser();

        var response = await LoginWithAsync(attacker, credential, ceremony: null);

        ((int)response.StatusCode).ShouldBeInRange(400, 499);
        attacker.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task A_tampered_ceremony_cookie_never_signs_anyone_in()
    {
        var victim = await VictimAsync();
        var (credential, ceremony) = await PrepareLoginAsync(victim);
        var attacker = App.NewBrowser();

        foreach (var tampered in new[] { ceremony[..^3] + "AAA", ceremony.ToUpperInvariant(), ceremony[1..], "x" + ceremony, "garbage", string.Empty })
        {
            var response = await LoginWithAsync(attacker, credential, tampered);
            ((int)response.StatusCode).ShouldBeInRange(400, 499, tampered);
        }

        attacker.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task An_assertion_made_for_one_challenge_is_refused_under_the_ceremony_of_another()
    {
        var victim = await VictimAsync();
        var (credential, _) = await PrepareLoginAsync(victim);
        var (_, otherCeremony) = await PrepareLoginAsync(victim); // a second ceremony, with a different challenge
        var attacker = App.NewBrowser();

        var response = await LoginWithAsync(attacker, credential, otherCeremony);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        attacker.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task A_finished_login_cannot_be_replayed_with_the_same_ceremony_and_assertion()
    {
        var victim = await VictimAsync();
        var (credential, ceremony) = await PrepareLoginAsync(victim);
        (await LoginWithAsync(victim, credential, ceremony)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var thief = App.NewBrowser();

        var replay = await LoginWithAsync(thief, credential, ceremony);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        thief.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task A_ceremony_older_than_ten_minutes_is_dead()
    {
        var victim = await VictimAsync();
        var (credential, ceremony) = await PrepareLoginAsync(victim);
        App.Time.Advance(CeremonyCookie.Lifetime + TimeSpan.FromSeconds(1));
        var attacker = App.NewBrowser();

        var response = await LoginWithAsync(attacker, credential, ceremony);

        ((int)response.StatusCode).ShouldBeInRange(400, 499);
        attacker.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task A_ceremony_started_to_create_a_passkey_cannot_finish_a_sign_in_and_the_other_way_round()
    {
        var (owner, _) = await SetUpOwnerAsync();

        // Signed in with a fresh passkey, the owner may add one: that ceremony is "create".
        var creation = await owner.PostAsync("/account/passkey/creation-options", new { });
        creation.StatusCode.ShouldBe(HttpStatusCode.OK, await creation.Content.ReadAsStringAsync());
        var createCeremony = CookieValue(owner, Ceremony);

        var victim = App.NewBrowser();
        victim.Authenticator = owner.Authenticator;
        var (credential, loginCeremony) = await PrepareLoginAsync(victim);

        // A "create" ceremony under /account/login.
        var attacker = App.NewBrowser();
        (await LoginWithAsync(attacker, credential, createCeremony)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A "sign-in" ceremony under /account/passkey/register.
        var options = (await creation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options");
        var created = JsonDocument.Parse(owner.Authenticator.Create(options.GetRawText())).RootElement.Clone();
        var swapped = await owner.SendAsync(HttpMethod.Post, "/account/passkey/register", new { credential = created, name = "Swapped" }, tweak: request =>
            request.Headers.Add("Cookie", $"{WebClient.SessionCookie}={CookieValue(owner, WebClient.SessionCookie)}; {Ceremony}={loginCeremony}"));
        ((int)swapped.StatusCode).ShouldBeInRange(400, 499);
        attacker.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task A_setup_code_works_once_even_if_the_registration_is_posted_twice_or_in_parallel()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var first = App.NewBrowser();
        var second = App.NewBrowser();
        var optionsFirst = await first.StartSetupAsync(code, "First");
        var optionsSecond = await second.StartSetupAsync(code, "Second");

        var outcomes = await Task.WhenAll(first.RegisterAsync(optionsFirst), second.RegisterAsync(optionsSecond));

        outcomes.Count(o => o.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        (await CountOwnersAsync()).ShouldBe(1);
        (await CountUsersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_registration_with_no_ceremony_or_a_tampered_one_makes_no_account_and_no_session()
    {
        await SetUpOwnerAsync();
        var users = await CountUsersAsync();
        var stranger = App.NewBrowser();

        var unprompted = await stranger.RegisterUnpromptedAsync();
        var tampered = await stranger.SendAsync(HttpMethod.Post, "/account/passkey/register", new { credential = new { }, name = "x" }, tweak: request =>
            request.Headers.Add("Cookie", $"{Ceremony}=AAAA"));

        ((int)unprompted.StatusCode).ShouldBeInRange(400, 499);
        ((int)tampered.StatusCode).ShouldBeInRange(400, 499);
        stranger.HasSessionCookie.ShouldBeFalse();
        (await CountUsersAsync()).ShouldBe(users);
    }
}
