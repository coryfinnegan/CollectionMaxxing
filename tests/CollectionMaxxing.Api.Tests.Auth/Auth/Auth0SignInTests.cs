using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance tests of AUTH-1 (issue #228): sign-in moves to Auth0. Do not edit, weaken, skip or delete: add new tests instead.
// Real sign-in throughout: the fake Auth0 tenant on loopback, the real OpenID Connect and JwtBearer handlers, real Postgres.

// Locked tests 1 and 2 of AUTH-1 (an unlinked identity gets nothing; the setup code makes the one owner) were retired by ONB-1 (#249):
// ADR-026 makes every first sign-in an account and ADR-027 removes the setup code. Their replacements are the locked ONB-1 tests in
// Onboarding/OnboardingAcceptanceTests.cs (AccountOnFirstSignInTests, SetupGoneAndRecoveryTests). People are still never matched by email.

/// <summary>Locked test 2 (recovery half): a recovery code links a new identity to the account it names, never makes or moves one.</summary>
public class Auth0RecoveryTests(ApiFixture api) : InviteTest(api)
{
    [Fact]
    public async Task A_recovery_code_links_a_new_identity_to_the_owner()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var lostEverything = App.NewBrowser(TestIdentity.Apple("Owner"));

        var response = await lostEverything.ContinueAsync(await lostEverything.StartRecoveryAsync(await App.IssueRecoveryCodeAsync(owner.Identity.Email!)));

        response.Headers.Location?.ToString().ShouldBe("/");
        (await lostEverything.MeAsync()).UserId.ShouldBe(account.UserId);
        (await CountOwnersAsync()).ShouldBe(1);
        (await CountUsersAsync()).ShouldBe(1);
        (await CountLoginsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_recovery_code_works_once()
    {
        var (owner, _) = await SetUpOwnerAsync();
        var code = await App.IssueRecoveryCodeAsync(owner.Identity.Email!);
        var first = App.NewBrowser();
        await first.ContinueAsync(await first.StartRecoveryAsync(code));

        await ShouldBeProblemAsync(await App.NewBrowser().StartRecoveryAsync(code), HttpStatusCode.Gone);
        (await CountLoginsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Recovery_never_takes_an_identity_away_from_a_member()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (member, memberAccount) = await AddMemberAsync(owner, account.LibraryId!.Value, "editor");
        var memberIdentity = member.Identity;

        var thief = App.NewBrowser(memberIdentity);
        var response = await thief.ContinueAsync(await thief.StartRecoveryAsync(await App.IssueRecoveryCodeAsync(owner.Identity.Email!)));

        response.Headers.Location?.ToString().ShouldBe("/?error=already_linked");
        thief.HasSessionCookie.ShouldBeFalse();
        await using var context = Db.CreateContext();
        (await context.Logins.SingleAsync(l => l.Subject == memberIdentity.Subject)).UserId.ShouldBe(memberAccount.UserId);
        (await CountOwnersAsync()).ShouldBe(1);
    }
}

/// <summary>
/// Locked test 3: an invite is accepted once, with exactly the invited role; expired and revoked invites fail. (ONB-1: joining is now
/// <c>POST /api/v1/invites:accept</c> after signing in, not a sign-in intent; the rules are the same.)
/// </summary>
public class Auth0InviteTests(ApiFixture api) : InviteTest(api)
{
    [Theory]
    [InlineData("viewer")]
    [InlineData("editor")]
    public async Task An_invite_makes_a_member_with_exactly_the_invited_role(string role)
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (token, _) = await CreateInviteAsync(owner, account.LibraryId!.Value, role);

        var (joiner, joined) = await JoinAsync(token);

        ShouldBeSignedIn(joiner, joined);
        var member = await joiner.MeAsync();
        (await RoleOfAsync(account.LibraryId!.Value, member.UserId)).ShouldBe(role);
        (await CountOwnersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task An_invite_is_accepted_once()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (token, _) = await CreateInviteAsync(owner, account.LibraryId!.Value);
        await JoinAsync(token);

        var (second, again) = await JoinAsync(token, "Alex");

        await ShouldBeInvalidInviteAsync(again);
        (await LibrariesOfAsync(second)).ShouldBeEmpty();
        (await CountMembersAsync(account.LibraryId!.Value)).ShouldBe(2);
    }

    [Fact]
    public async Task Two_people_who_both_started_with_the_same_invite_make_one_member()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (token, _) = await CreateInviteAsync(owner, account.LibraryId!.Value, "editor");
        var first = App.NewBrowser();
        var second = App.NewBrowser();
        await first.LoginAsync();
        await second.LoginAsync();

        var answers = await Task.WhenAll(first.JoinAsync(token), second.JoinAsync(token));

        answers.Count(a => a.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        answers.Count(a => a.StatusCode == HttpStatusCode.NotFound).ShouldBe(1);
        (await CountMembersAsync(account.LibraryId!.Value)).ShouldBe(2);
    }

    [Fact]
    public async Task An_invite_that_expires_while_the_person_is_at_Auth0_fails()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (token, _) = await CreateInviteAsync(owner, account.LibraryId!.Value);
        App.Time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        var joiner = App.NewBrowser();
        (await joiner.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        App.Time.Advance(TimeSpan.FromMinutes(2));

        var response = await joiner.JoinAsync(token);

        await ShouldBeInvalidInviteAsync(response);
        (await CountMembersAsync(account.LibraryId!.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task An_expired_invite_fails()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (token, _) = await CreateInviteAsync(owner, account.LibraryId!.Value);
        App.Time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));

        var (joiner, response) = await JoinAsync(token);

        await ShouldBeInvalidInviteAsync(response);
        (await LibrariesOfAsync(joiner)).ShouldBeEmpty();
        (await CountMembersAsync(account.LibraryId!.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task A_revoked_invite_fails()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (token, created) = await CreateInviteAsync(owner, account.LibraryId!.Value);
        var inviteId = created.GetProperty("invite").GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Delete, $"{InvitesUrl(account.LibraryId!.Value)}/{inviteId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var (joiner, response) = await JoinAsync(token);

        await ShouldBeInvalidInviteAsync(response);
        (await LibrariesOfAsync(joiner)).ShouldBeEmpty();
        (await CountMembersAsync(account.LibraryId!.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task An_identity_that_already_has_an_account_does_not_get_a_second_one_from_an_invite()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (token, _) = await CreateInviteAsync(owner, account.LibraryId!.Value, "editor");

        var response = await App.NewBrowser(owner.Identity).JoinAsync(token);

        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict);
        (await CountUsersAsync()).ShouldBe(1);
        (await CountMembersAsync(account.LibraryId!.Value)).ShouldBe(1);
    }

    private static async Task<JsonElement[]> LibrariesOfAsync(WebClient browser) =>
        [.. (await ReadAsync(await browser.GetAsync("/api/v1/libraries"))).GetProperty("items").EnumerateArray()];
}

/// <summary>Locked tests 4 and 5: bearer tokens are checked strictly, and a deactivated user's token stops working.</summary>
public class Auth0BearerTests(ApiFixture api) : InviteTest(api)
{
    public static TheoryData<string> BadTokens => ["wrong issuer", "wrong audience", "expired", "bad signature", "alg none", "HS256"];

    [Fact]
    public async Task A_valid_token_of_a_linked_identity_is_the_person_it_is_linked_to()
    {
        var (owner, account) = await SetUpOwnerAsync();

        var response = await PhoneOf(owner).GetAsync("/api/v1/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(response)).GetProperty("id").GetGuid().ShouldBe(account.UserId);
    }

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task A_bad_token_is_401(string kind)
    {
        var (owner, _) = await SetUpOwnerAsync();
        var subject = owner.Identity.Subject;
        var auth0 = App.Auth0;
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        var token = kind switch
        {
            "wrong issuer" => auth0.AccessToken(subject, c => c["iss"] = "https://evil.auth0.test/"),
            "wrong audience" => auth0.Mint(FakeAuth0.WebClientId, claims), // an ID token for the web app is not an access token for the API
            "expired" => auth0.AccessToken(subject, c =>
            {
                c["iat"] = EpochTime.GetIntDate(DateTime.UtcNow.AddHours(-2));
                c["nbf"] = EpochTime.GetIntDate(DateTime.UtcNow.AddHours(-2));
                c["exp"] = EpochTime.GetIntDate(DateTime.UtcNow.AddMinutes(-5));
            }),
            "bad signature" => auth0.Mint(FakeAuth0.Audience, claims, new SigningCredentials(new RsaSecurityKey(TestSigningKey.Other) { KeyId = TestSigningKey.KeyId }, SecurityAlgorithms.RsaSha256)),
            "alg none" => Unsigned(subject),
            "HS256" => auth0.Mint(FakeAuth0.Audience, claims, new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(new string('k', 64))), SecurityAlgorithms.HmacSha256)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var response = await App.NewPhoneWithToken(token).GetAsync("/api/v1/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, kind);
    }

    [Fact]
    public async Task A_deactivated_users_otherwise_valid_token_is_401_on_their_next_call()
    {
        // ONB-1: removing a member ends only that membership (LeaveAndRemoveTests); deleting the account is what deactivates it.
        var (owner, account) = await SetUpOwnerAsync();
        var (member, _) = await AddMemberAsync(owner, account.LibraryId!.Value, "editor");
        var phone = PhoneOf(member);
        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await member.SendAsync(HttpMethod.Delete, "/api/v1/me", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await phone.GetAsync("/api/v1/me");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadAsync(response)).GetProperty("code").GetString().ShouldBe("account.deactivated");

        // Nor can they sign in on the web again with the same identity.
        var again = App.NewBrowser(member.Identity);
        (await again.SignInAsync()).Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");
        again.HasSessionCookie.ShouldBeFalse();
    }

    private static string Unsigned(string subject)
    {
        static string Part(object value) => Base64UrlEncoder.Encode(JsonSerializer.Serialize(value));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Part(new { alg = "none", typ = "JWT" }) + "."
            + Part(new { iss = FakeAuth0.Issuer, aud = FakeAuth0.Audience, sub = subject, iat = now, exp = now + 3600 }) + ".";
    }
}

/// <summary>Locked test 6: <c>returnUrl</c> must be a page of this site.</summary>
public class ReturnUrlTests(ApiFixture api) : AuthTest(api)
{
    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("//evil.test")]
    [InlineData("/\\evil.test")]
    [InlineData("\\\\evil.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("evil.test")]
    public async Task A_non_local_return_url_is_refused(string returnUrl)
    {
        var browser = App.NewBrowser();

        var response = await browser.GetAsync("/account/login?returnUrl=" + Uri.EscapeDataString(returnUrl));

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest);
        (await ReadAsync(response)).GetProperty("code").GetString().ShouldBe("account.bad_return_url");
        browser.Cookie(SignInIntentCookie.Name).ShouldBeNull();
    }

    [Fact]
    public async Task A_local_return_url_is_where_the_sign_in_ends()
    {
        var (owner, _) = await SetUpOwnerAsync();
        owner.ClearCookies();

        var response = await owner.SignInAsync("/account/login?returnUrl=" + Uri.EscapeDataString("/library?tab=games"));

        response.Headers.Location?.ToString().ShouldBe("/library?tab=games");
        owner.HasSessionCookie.ShouldBeTrue();
    }
}

/// <summary>Locked test 7: linking needs a signed-in user with a recent sign-in; the last sign-in method can't be removed.</summary>
public class LinkingTests(ApiFixture api) : InviteTest(api)
{
    private const string Methods = "/api/v1/me/sign-in-methods";

    [Fact]
    public async Task Linking_without_a_session_sends_the_browser_to_sign_in()
    {
        await SetUpOwnerAsync();
        var browser = App.NewBrowser();

        var response = await browser.GetAsync("/account/login?link=1");

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location?.ToString().ShouldBe("/login");
        (await CountLoginsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Linking_with_a_recent_sign_in_adds_a_second_method()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var apple = TestIdentity.Apple("Owner");

        var response = await owner.LinkAsync(apple);

        response.Headers.Location?.ToString().ShouldBe("/");
        await using var context = Db.CreateContext();
        (await context.Logins.SingleAsync(l => l.Subject == apple.Subject)).UserId.ShouldBe(account.UserId);
        (await App.NewBrowser(apple).SignInAsync()).Headers.Location?.ToString().ShouldBe("/");
        owner.LastChallenge!.Query.ShouldContain("max_age=0");
        owner.LastChallenge!.Query.ShouldContain("prompt=login");
    }

    [Fact]
    public async Task Linking_without_a_recent_sign_in_links_nothing()
    {
        var (owner, _) = await SetUpOwnerAsync();
        App.Time.Advance(TimeSpan.FromMinutes(6));

        var response = await owner.LinkAsync(TestIdentity.Apple());

        response.Headers.Location?.ToString().ShouldBe("/?error=reauth_failed");
        (await CountLoginsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task An_identity_linked_to_someone_else_is_not_moved()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (member, memberAccount) = await AddMemberAsync(owner, account.LibraryId!.Value, "viewer");

        var response = await owner.LinkAsync(member.Identity);

        response.Headers.Location?.ToString().ShouldBe("/?error=already_linked");
        await using var context = Db.CreateContext();
        (await context.Logins.SingleAsync(l => l.Subject == member.Identity.Subject)).UserId.ShouldBe(memberAccount.UserId);
    }

    [Fact]
    public async Task The_last_sign_in_method_cannot_be_removed()
    {
        var (owner, _) = await SetUpOwnerAsync();
        var only = (await ReadAsync(await owner.GetAsync(Methods))).GetProperty("items")[0].GetProperty("id").GetGuid();

        var response = await owner.SendAsync(HttpMethod.Delete, $"{Methods}/{only}", null);

        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict);
        (await ReadAsync(response)).GetProperty("code").GetString().ShouldBe("account.last_sign_in_method");
        (await CountLoginsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Removing_a_sign_in_method_needs_a_recent_sign_in()
    {
        var (owner, _) = await SetUpOwnerAsync();
        await owner.LinkAsync(TestIdentity.Apple());
        var apple = (await ReadAsync(await owner.GetAsync(Methods))).GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("provider").GetString() == "apple").GetProperty("id").GetGuid();
        App.Time.Advance(TimeSpan.FromMinutes(6));

        var stale = await owner.SendAsync(HttpMethod.Delete, $"{Methods}/{apple}", null);
        await ShouldBeProblemAsync(stale, HttpStatusCode.Unauthorized);
        (await ReadAsync(stale)).GetProperty("code").GetString().ShouldBe("reauth_required");
        (await CountLoginsAsync()).ShouldBe(2);

        (await owner.ReauthAsync()).Headers.Location?.ToString().ShouldBe("/");
        (await owner.SendAsync(HttpMethod.Delete, $"{Methods}/{apple}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await CountLoginsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Somebody_elses_sign_in_method_is_not_found()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (member, _) = await AddMemberAsync(owner, account.LibraryId!.Value, "viewer");
        var theirs = (await ReadAsync(await member.GetAsync(Methods))).GetProperty("items")[0].GetProperty("id").GetGuid();

        await ShouldBeProblemAsync(await owner.SendAsync(HttpMethod.Delete, $"{Methods}/{theirs}", null), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_the_account_needs_a_recent_sign_in()
    {
        var (owner, account) = await SetUpOwnerAsync();
        App.Time.Advance(TimeSpan.FromMinutes(6));

        var stale = await owner.SendAsync(HttpMethod.Delete, "/api/v1/me", null);

        await ShouldBeProblemAsync(stale, HttpStatusCode.Unauthorized);
        (await ReadAsync(stale)).GetProperty("code").GetString().ShouldBe("reauth_required");
        await using (var context = Db.CreateContext())
        {
            (await context.Users.SingleAsync(u => u.Id == account.UserId)).DeletedAt.ShouldBeNull();
        }

        await owner.ReauthAsync();
        (await owner.SendAsync(HttpMethod.Delete, "/api/v1/me", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_bearer_token_never_counts_as_a_recent_sign_in()
    {
        var (owner, _) = await SetUpOwnerAsync();

        var response = await PhoneOf(owner).DeleteAsync("/api/v1/me");

        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized);
        (await ReadAsync(response)).GetProperty("code").GetString().ShouldBe("reauth_required");
    }
}

/// <summary>Locked test 8: no OpenIddict, passkey or pairing route is left, and the new routes are where the matrix expects them.</summary>
public class NoOldSignInRoutesTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public void No_OpenIddict_passkey_or_pairing_route_remains()
    {
        var routes = App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.ShouldNotContain(r => r.StartsWith("/connect", StringComparison.OrdinalIgnoreCase));
        routes.ShouldNotContain(r => r.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase));
        routes.ShouldNotContain(r => r.Contains("passkey", StringComparison.OrdinalIgnoreCase));
        routes.ShouldNotContain(r => r.Contains("pair", StringComparison.OrdinalIgnoreCase));
        routes.ShouldNotContain(r => r.StartsWith("/api/v1/me/sessions", StringComparison.OrdinalIgnoreCase));
        routes.ShouldContain("/api/v1/me/sign-in-methods");
        routes.ShouldContain("/api/v1/me/sign-in-methods/{id:guid}");
        routes.ShouldNotContain("/account/not-linked"); // ONB-1: a first sign-in makes the account, so nobody lands there
        routes.ShouldNotContain("/setup");
    }
}

/// <summary>The rules around the callback: the OpenID Connect handler never signs anyone in, and the intent must be this challenge's own.</summary>
public class CallbackTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public void The_OpenID_Connect_handler_signs_in_with_a_scheme_that_refuses()
    {
        var options = App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(Auth0Setup.OidcScheme);

        options.SignInScheme.ShouldBe(Auth0Setup.NoSignInScheme);
        options.SaveTokens.ShouldBeFalse();
        options.UsePkce.ShouldBeTrue();
    }

    [Fact]
    public async Task A_callback_without_the_sealed_intent_signs_nobody_in()
    {
        var (owner, _) = await SetUpOwnerAsync();
        owner.ClearCookies();
        var start = await owner.GetAsync("/account/login");
        var query = owner.Challenge(start)!;
        owner.ForgetCookie(SignInIntentCookie.Name);
        var code = Guid.NewGuid().ToString("N");
        App.Auth0.ExpectCode(code, query["nonce"]!, owner.Identity, App.Time.GetUtcNow());

        var response = await owner.GetAsync($"/signin-oidc?code={code}&state={Uri.EscapeDataString(query["state"]!)}");

        response.Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");
        owner.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task An_intent_from_another_sign_in_is_not_accepted()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (token, _) = await InviteTestHelpers.CreateInviteAsync(owner, account.LibraryId!.Value);

        // The attacker starts a join in their own browser, then makes the victim's callback carry that intent.
        var attacker = App.NewBrowser();
        await attacker.GetAsync("/account/login?invite=" + token);
        var swapped = attacker.Cookie(SignInIntentCookie.Name)!;

        var victim = App.NewBrowser();
        var query = victim.Challenge(await victim.GetAsync("/account/login"))!;
        victim.UseCookie(SignInIntentCookie.Name, swapped);
        var code = Guid.NewGuid().ToString("N");
        App.Auth0.ExpectCode(code, query["nonce"]!, victim.Identity, App.Time.GetUtcNow());

        var response = await victim.GetAsync($"/signin-oidc?code={code}&state={Uri.EscapeDataString(query["state"]!)}");

        response.Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");
        (await CountUsersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task An_id_token_with_the_wrong_nonce_signs_nobody_in()
    {
        var (owner, _) = await SetUpOwnerAsync();
        owner.ClearCookies();

        var response = await owner.SignInAsync(tweak: claims => claims["nonce"] = "not-the-challenges");

        response.Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");
        owner.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_sign_in_goes_back_to_the_sign_in_page()
    {
        var browser = App.NewBrowser();
        var query = browser.Challenge(await browser.GetAsync("/account/login"))!;

        var response = await browser.GetAsync($"/signin-oidc?error=access_denied&state={Uri.EscapeDataString(query["state"]!)}");

        response.Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");
        browser.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task The_callback_address_comes_from_the_public_origin_not_the_host_header()
    {
        var browser = App.NewBrowser();

        var start = await browser.SendAsync(HttpMethod.Get, "/account/login", null, tweak: r => r.Headers.Host = "evil.test");

        browser.Challenge(start)!["redirect_uri"].ShouldBe(FakeAuth0.PublicOrigin + "/signin-oidc");
    }

    [Fact]
    public async Task Signing_out_without_a_session_answers_the_Auth0_logout_address_and_changes_nothing()
    {
        await SetUpOwnerAsync();
        var (users, logins) = (await CountUsersAsync(), await CountLoginsAsync());
        var browser = App.NewBrowser();

        var response = await browser.PostAsync("/account/logout");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var url = (await ReadAsync(response)).GetProperty("logoutUrl").GetString()!;
        url.ShouldBe($"{FakeAuth0.Issuer}v2/logout?client_id={FakeAuth0.WebClientId}&returnTo={Uri.EscapeDataString(FakeAuth0.PublicOrigin)}");
        (await CountUsersAsync()).ShouldBe(users);
        (await CountLoginsAsync()).ShouldBe(logins);
        (await browser.PostAsync("/account/logout", csrf: false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Signing_out_revokes_the_session()
    {
        var (owner, _) = await SetUpOwnerAsync();
        var copied = owner.SessionCookieValue!;

        (await owner.PostAsync("/account/logout")).StatusCode.ShouldBe(HttpStatusCode.OK);

        owner.HasSessionCookie.ShouldBeFalse();
        var thief = App.NewBrowser();
        thief.UseSessionCookie(copied);
        (await thief.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

/// <summary>Outside Development the API refuses to start without its Auth0 settings, and ignores the old passkey and OpenIddict ones.</summary>
public class Auth0SettingsTests(ApiFixture api) : ApiTest(api)
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-auth0-settings-" + Guid.NewGuid().ToString("N"));

    public override async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("Auth:Auth0:Domain")]
    [InlineData("Auth:Auth0:Audience")]
    [InlineData("Auth:Auth0:WebClientId")]
    [InlineData("Auth:Auth0:WebClientSecret")]
    [InlineData("Auth:PublicOrigin")]
    public void Starting_in_production_without_a_sign_in_setting_fails_loudly(string missing)
    {
        var settings = FakeAuth0.Settings;
        settings[missing] = null;
        using var factory = ProductionFactory(settings);

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        failure.ToString().ShouldContain(missing);
    }

    [Fact]
    public void Starting_in_production_with_the_sign_in_settings_works_and_the_old_ones_are_ignored()
    {
        var settings = FakeAuth0.Settings;
        settings["Auth:RelyingPartyId"] = "pc.tailnet.ts.net";
        settings["Auth:Keys:SigningCertificatePath"] = "/secrets/openiddict-signing.pfx";
        settings["Auth:Keys:EncryptionCertificatePath"] = "/secrets/openiddict-encryption.pfx";
        using var factory = ProductionFactory(settings);

        using var client = factory.CreateClient();
    }

    private WebApplicationFactory<Program> ProductionFactory(Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                settings["ConnectionStrings:collectionmaxxing"] = Db.ConnectionString;
                settings["Auth:SecretsPath"] = Path.Combine(_root, "secrets");
                settings["Auth:KeyRing:Path"] = Path.Combine(_root, "keys");
                settings["Auth:Setup:IssueOnStartup"] = "false";
                configuration.AddInMemoryCollection(settings);
            });
        });
}

/// <summary>Invite helpers for tests that aren't household tests.</summary>
internal static class InviteTestHelpers
{
    public static async Task<(string Token, JsonElement Created)> CreateInviteAsync(WebClient owner, Guid libraryId, string role = "viewer")
    {
        var response = await owner.PostAsync($"/api/v1/libraries/{libraryId}/invites", new { name = "Sam", role });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var link = created.GetProperty("link").GetString()!;
        return (link[(link.LastIndexOf('/') + 1)..], created);
    }
}
