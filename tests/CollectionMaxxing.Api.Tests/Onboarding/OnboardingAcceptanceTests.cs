using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Authorization;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Onboarding;

// LOCKED acceptance tests of ONB-1 (issue #249, docs/15 "Locked acceptance tests for ONB-1"). Do not edit, weaken, skip or delete: add
// new tests instead. Real sign-in throughout: the fake Auth0 tenant on loopback (discovery, JWKS, token, /userinfo), real Postgres.

/// <summary>Locked test 1: a first sign-in creates exactly one account and links its <c>sub</c>, on the web and with a bearer token.</summary>
public partial class AccountOnFirstSignInTests(ApiFixture api) : OnboardingTest(api)
{
    [Fact]
    public async Task A_first_web_sign_in_creates_one_account_with_the_name_and_email_from_the_id_token()
    {
        var who = new TestIdentity("google-oauth2|" + Guid.NewGuid().ToString("N"), "ana@example.com", "Ana");
        var browser = App.NewBrowser(who);

        var response = await browser.SignInAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location?.ToString().ShouldBe("/");
        browser.HasSessionCookie.ShouldBeTrue();
        var me = await browser.MeAsync();
        me.LibraryId.ShouldBeNull();
        (await CountUsersAsync()).ShouldBe(1);
        await using var context = Db.CreateContext();
        var login = await context.Logins.SingleAsync();
        (login.UserId, login.Subject).ShouldBe((me.UserId, who.Subject));
        (await UserAsync(me.UserId)).ShouldBe(("Ana", "ana@example.com"));
    }

    [Fact]
    public async Task A_first_web_sign_in_ends_at_the_return_url()
    {
        var browser = App.NewBrowser();

        var response = await browser.SignInAsync("/account/login?returnUrl=" + Uri.EscapeDataString("/join/abc"));

        response.Headers.Location?.ToString().ShouldBe("/join/abc");
        browser.HasSessionCookie.ShouldBeTrue();
    }

    [Fact]
    public async Task A_first_bearer_call_creates_one_account_with_the_name_and_email_from_userinfo_never_from_the_request()
    {
        var who = new TestIdentity("apple|" + Guid.NewGuid().ToString("N"), "sam@example.com", "Sam");
        App.Auth0.SetProfile(who);

        // The token itself says otherwise: only Auth0's /userinfo is believed.
        var token = App.Auth0.AccessToken(who.Subject, claims =>
        {
            claims["name"] = "Mallory";
            claims["email"] = "mallory@example.com";
        });
        using var phone = App.NewPhoneWithToken(token);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Add("X-Name", "Mallory");

        var response = await phone.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await ReadAsync(response);
        body.GetProperty("memberships").GetArrayLength().ShouldBe(0);
        var userId = body.GetProperty("id").GetGuid();
        (await UserAsync(userId)).ShouldBe(("Sam", "sam@example.com"));
        await using var context = Db.CreateContext();
        (await context.Logins.SingleAsync()).Subject.ShouldBe(who.Subject);
        App.Auth0.UserInfoCalls(who.Subject).ShouldBe(1);

        // The next call is the same person, and Auth0 isn't asked again.
        (await ReadAsync(await phone.GetAsync("/api/v1/me"))).GetProperty("id").GetGuid().ShouldBe(userId);
        App.Auth0.UserInfoCalls(who.Subject).ShouldBe(1);
        (await CountUsersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_bearer_call_creates_nothing_when_userinfo_cannot_be_reached_and_the_next_call_does()
    {
        var who = TestIdentity.Google("Sam");
        App.Auth0.SetProfile(who);
        App.Auth0.UserInfoDown = true;
        using var phone = App.NewPhone(who.Subject);

        var down = await phone.GetAsync("/api/v1/me");

        ((int)down.StatusCode).ShouldBe(503, await down.Content.ReadAsStringAsync());
        (await CountUsersAsync()).ShouldBe(0);

        App.Auth0.UserInfoDown = false;
        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountUsersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_second_sign_in_reuses_the_account_on_the_web_and_with_a_bearer_token()
    {
        var who = TestIdentity.Google("Ana");
        var first = await App.NewBrowser(who).SignInAsync();
        first.Headers.Location?.ToString().ShouldBe("/");
        var again = App.NewBrowser(who);
        await again.SignInAsync();
        var me = await again.MeAsync();

        var phone = await App.NewPhone(who.Subject).GetAsync("/api/v1/me");

        (await ReadAsync(phone)).GetProperty("id").GetGuid().ShouldBe(me.UserId);
        (await CountUsersAsync()).ShouldBe(1);
        (await CountLoginsAsync()).ShouldBe(1);
        App.Auth0.UserInfoCalls(who.Subject).ShouldBe(0);
    }

    [Fact]
    public async Task Two_first_calls_at_once_make_one_account()
    {
        var who = TestIdentity.Apple("Sam");
        App.Auth0.SetProfile(who);
        var phones = Enumerable.Range(0, 6).Select(_ => App.NewPhone(who.Subject)).ToList();

        var responses = await Task.WhenAll(phones.Select(p => p.GetAsync("/api/v1/me")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var ids = await Task.WhenAll(responses.Select(async r => (await ReadAsync(r)).GetProperty("id").GetGuid()));
        ids.Distinct().Count().ShouldBe(1);
        (await CountUsersAsync()).ShouldBe(1);
        (await CountLoginsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_web_and_a_bearer_first_sign_in_at_once_make_one_account()
    {
        var who = TestIdentity.Google("Ana");
        App.Auth0.SetProfile(who);
        var browser = App.NewBrowser(who);
        var started = await browser.GetAsync("/account/login");

        var web = browser.FinishAsync(started);
        var bearer = App.NewPhone(who.Subject).GetAsync("/api/v1/me");
        await Task.WhenAll(web, bearer);

        (await bearer).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.MeAsync()).UserId.ShouldBe((await ReadAsync(await bearer)).GetProperty("id").GetGuid());
        (await CountUsersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_new_sub_with_an_existing_accounts_email_gets_a_new_account()
    {
        var (_, ana) = await NewAccountAsync("Ana");
        var anaEmail = (await UserAsync(ana.UserId)).Email;
        var sameEmail = new TestIdentity("apple|" + Guid.NewGuid().ToString("N"), anaEmail, "Ana");

        var browser = App.NewBrowser(sameEmail);
        (await browser.SignInAsync()).Headers.Location?.ToString().ShouldBe("/");

        (await browser.MeAsync()).UserId.ShouldNotBe(ana.UserId);
        (await CountUsersAsync()).ShouldBe(2);
        await using var context = Db.CreateContext();
        (await context.Logins.CountAsync(l => l.UserId == ana.UserId)).ShouldBe(1);
    }
}

/// <summary>Locked test 1 (limits): account creation is limited per client, on the web and with a bearer token.</summary>
public class AccountCreationLimitTests(ApiFixture api) : OnboardingTest(api)
{
    protected override Dictionary<string, string?>? MoreSettings => new() { ["Auth:RateLimit:NewAccountsPerHour"] = "2" };

    [Fact]
    public async Task A_client_that_makes_too_many_accounts_is_refused_and_nothing_is_created()
    {
        var (first, _) = await NewAccountAsync("One");
        await NewAccountAsync("Two");

        var third = App.NewBrowser();
        var web = await third.SignInAsync();
        var bearer = await App.NewPhone(TestIdentity.Google().Subject).GetAsync("/api/v1/me");

        web.Headers.Location?.ToString().ShouldBe("/login?error=rate_limited");
        third.HasSessionCookie.ShouldBeFalse();
        await ShouldBeCodeAsync(bearer, HttpStatusCode.TooManyRequests, "rate_limited");
        (await CountUsersAsync()).ShouldBe(2);

        // People who already have an account still sign in.
        first.ClearCookies();
        (await first.SignInAsync()).Headers.Location?.ToString().ShouldBe("/");
    }
}

/// <summary>Locked test 2: an account with no libraries reaches its own data only.</summary>
public class NoLibrariesTests(ApiFixture api) : OnboardingTest(api)
{
    [Fact]
    public async Task An_account_with_no_libraries_gets_its_me_an_empty_list_and_404_on_another_librarys_routes()
    {
        var (owner, _) = await NewAccountAsync("Cory");
        var library = await CreatedLibraryAsync(owner, "Cory's library");
        var (newcomer, _) = await NewAccountAsync("Ana");

        var me = await newcomer.GetAsync("/api/v1/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(me)).GetProperty("memberships").GetArrayLength().ShouldBe(0);
        (await LibraryIdsAsync(newcomer)).ShouldBeEmpty();

        foreach (var url in new[] { LibraryUrl(library), MembersUrl(library), InvitesUrl(library), $"{LibraryUrl(library)}/items", $"{LibraryUrl(library)}/locations" })
        {
            await ShouldBeProblemAsync(await newcomer.GetAsync(url), HttpStatusCode.NotFound);
        }

        await ShouldBeProblemAsync(await newcomer.PostAsync(InvitesUrl(library), new { name = "Me", role = "editor" }), HttpStatusCode.NotFound);
        await ShouldBeProblemAsync(await newcomer.SendAsync(HttpMethod.Delete, MembershipUrl(library), null), HttpStatusCode.NotFound);
        await ShouldBeProblemAsync(await App.NewPhone(newcomer.Identity.Subject).GetAsync(LibraryUrl(library)), HttpStatusCode.NotFound);
    }
}

/// <summary>Locked test 3: any account can create a library and is its only owner, up to the limit; nobody else can see it.</summary>
public class CreateLibraryTests(ApiFixture api) : OnboardingTest(api)
{
    [Fact]
    public async Task Any_account_can_create_a_library_and_becomes_its_only_owner()
    {
        var (ana, account) = await NewAccountAsync("Ana");

        var response = await CreateLibraryAsync(ana, "  Ana's games  ");

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var library = await ReadAsync(response);
        var id = library.GetProperty("id").GetGuid();
        library.GetProperty("name").GetString().ShouldBe("Ana's games");
        library.GetProperty("role").GetString().ShouldBe("owner");
        response.Headers.Location?.ToString().ShouldBe($"/api/v1/libraries/{id}");
        (await LibraryIdsAsync(ana)).ShouldBe([id]);
        (await RoleOfAsync(id, account.UserId)).ShouldBe("owner");
        (await CountMembersAsync(id)).ShouldBe(1);
        (await App.NewPhone(ana.Identity.Subject).GetAsync(LibraryUrl(id))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_fourth_owned_library_is_refused_with_library_limit_reached()
    {
        var (ana, _) = await NewAccountAsync("Ana");
        for (var i = 1; i <= 3; i++)
        {
            await CreatedLibraryAsync(ana, $"Library {i}");
        }

        var fourth = await CreateLibraryAsync(ana, "Library 4");

        await ShouldBeCodeAsync(fourth, HttpStatusCode.Conflict, "library.limit_reached");
        (await LibraryIdsAsync(ana)).Length.ShouldBe(3);
    }

    [Fact]
    public async Task Libraries_joined_by_invite_do_not_count_against_the_limit()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var corys = await CreatedLibraryAsync(cory, "Cory's library");
        var (ana, _) = await NewAccountAsync("Ana");
        var (token, _) = await CreateInviteAsync(cory, corys, "editor");
        (await AcceptAsync(ana, token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        for (var i = 1; i <= 3; i++)
        {
            await CreatedLibraryAsync(ana, $"Library {i}");
        }

        (await LibraryIdsAsync(ana)).Length.ShouldBe(4);
    }

    [Fact]
    public async Task Concurrent_creates_never_pass_the_limit()
    {
        var (ana, account) = await NewAccountAsync("Ana");

        var responses = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => CreateLibraryAsync(ana, $"Library {i}")));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(3);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(3);
        await using var context = Db.CreateContext();
        (await context.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.UserId == account.UserId && m.Role == LibraryRole.Owner)).ShouldBe(3);
    }

    [Theory]
    [InlineData(null, "library.name_required")]
    [InlineData("   ", "library.name_required")]
    [InlineData("81", "library.name_too_long")]
    public async Task A_name_must_be_1_to_80_characters(string? name, string code)
    {
        var (ana, _) = await NewAccountAsync("Ana");

        var response = await CreateLibraryAsync(ana, name == "81" ? new string('x', 81) : name);

        await ShouldBeCodeAsync(response, HttpStatusCode.BadRequest, code);
        (await LibraryIdsAsync(ana)).ShouldBeEmpty();
        (await CreateLibraryAsync(ana, new string('x', 80))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Another_account_cannot_see_or_reach_the_new_library()
    {
        var (ana, _) = await NewAccountAsync("Ana");
        var id = await CreatedLibraryAsync(ana);
        var (sam, _) = await NewAccountAsync("Sam");

        (await LibraryIdsAsync(sam)).ShouldBeEmpty();
        await ShouldBeProblemAsync(await sam.GetAsync(LibraryUrl(id)), HttpStatusCode.NotFound);
        await ShouldBeProblemAsync(await sam.SendAsync(HttpMethod.Patch, LibraryUrl(id), new { name = "Mine now" }), HttpStatusCode.NotFound);
        await ShouldBeProblemAsync(await sam.GetAsync(MembersUrl(id)), HttpStatusCode.NotFound);
        await ShouldBeProblemAsync(await sam.PostAsync(InvitesUrl(id), new { name = "Me", role = "editor" }), HttpStatusCode.NotFound);
    }
}

/// <summary>Locked test 4: joining by link or code gives exactly the invited role, once, never Owner; bad invites all look the same.</summary>
public partial class JoinByInviteTests(ApiFixture api) : OnboardingTest(api)
{
    public static TheoryData<string, string> Ways => new()
    {
        { "link", "viewer" }, { "token", "editor" }, { "code", "viewer" }, { "messy code", "editor" },
    };

    [Theory]
    [MemberData(nameof(Ways))]
    public async Task Joining_by_link_token_or_code_gives_exactly_the_invited_role(string way, string role)
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var library = await CreatedLibraryAsync(cory, "Cory's library");
        var (token, created) = await CreateInviteAsync(cory, library, role);
        var code = CodeOf(created);
        var invite = way switch
        {
            "link" => created.GetProperty("link").GetString()!,
            "token" => token,
            "code" => code,
            _ => Messy(code),
        };
        var (ana, account) = await NewAccountAsync("Ana");

        var preview = await PreviewAsync(ana, invite);
        preview.StatusCode.ShouldBe(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        var shown = await ReadAsync(preview);
        (shown.GetProperty("libraryName").GetString(), shown.GetProperty("ownerName").GetString(), shown.GetProperty("role").GetString())
            .ShouldBe(("Cory's library", "Cory", role));

        var accepted = await AcceptAsync(ana, invite);

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        var summary = await ReadAsync(accepted);
        (summary.GetProperty("id").GetGuid(), summary.GetProperty("name").GetString(), summary.GetProperty("role").GetString())
            .ShouldBe((library, "Cory's library", role));
        (await RoleOfAsync(library, account.UserId)).ShouldBe(role);
        (await LibraryIdsAsync(ana)).ShouldBe([library]);
        (await CountOwnersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_code_is_shown_once_as_ABCD_EFGH_JK_and_only_its_hash_is_stored()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var library = await CreatedLibraryAsync(cory);

        var (_, created) = await CreateInviteAsync(cory, library);

        var code = CodeOf(created);
        CodeFormat().IsMatch(code).ShouldBeTrue(code);
        var listed = await BodyOfAsync(await cory.GetAsync(InvitesUrl(library)));
        listed.ShouldNotContain(code);
        listed.ShouldNotContain(code.Replace("-", string.Empty, StringComparison.Ordinal));
        await using var context = Db.CreateContext();
        var stored = await context.LibraryInvites.IgnoreQueryFilters().SingleAsync();
        stored.CodeHash.ShouldNotBeNull();
        stored.CodeHash.ShouldNotContain(code.Replace("-", string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("link")]
    public async Task An_invite_is_accepted_once(string way)
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var library = await CreatedLibraryAsync(cory);
        var (_, created) = await CreateInviteAsync(cory, library, "editor");
        var invite = way == "code" ? CodeOf(created) : created.GetProperty("link").GetString()!;
        var (ana, _) = await NewAccountAsync("Ana");
        var (sam, _) = await NewAccountAsync("Sam");
        (await AcceptAsync(ana, invite)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await ShouldBeCodeAsync(await AcceptAsync(sam, invite), HttpStatusCode.NotFound, "invite.invalid");
        await ShouldBeCodeAsync(await PreviewAsync(sam, invite), HttpStatusCode.NotFound, "invite.invalid");
        (await CountMembersAsync(library)).ShouldBe(2);
        (await LibraryIdsAsync(sam)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_accounts_accepting_one_invite_at_once_make_one_member()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var library = await CreatedLibraryAsync(cory);
        var (_, created) = await CreateInviteAsync(cory, library, "editor");
        var joiners = new List<Auth.WebClient>();
        for (var i = 0; i < 4; i++)
        {
            joiners.Add((await NewAccountAsync($"Joiner {i}")).Browser);
        }

        var responses = await Task.WhenAll(joiners.Select(j => AcceptAsync(j, CodeOf(created))));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.NotFound).ShouldBe(3);
        (await CountMembersAsync(library)).ShouldBe(2);
    }

    [Fact]
    public async Task An_invite_never_grants_owner()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var library = await CreatedLibraryAsync(cory);

        await ShouldBeProblemAsync(await InviteAsync(cory, library, "owner"), HttpStatusCode.BadRequest);

        // Even an invite row that says "owner" (the table refuses it) could never be accepted as one.
        var coryId = (await cory.MeAsync()).UserId;
        await using var context = Db.CreateContext();
        var forged = () => context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO library_invites (id, library_id, role, name, token_hash, code_hash, created_by, created_at, expires_at) VALUES ({Guid.CreateVersion7()}, {library}, 'owner', 'x', 'h', 'c', {coryId}, now(), now() + interval '1 day')");
        (await Should.ThrowAsync<Npgsql.PostgresException>(forged)).SqlState.ShouldBe(Npgsql.PostgresErrorCodes.CheckViolation);
        (await CountOwnersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_expired_revoked_and_used_invites_fail_the_same_way()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var library = await CreatedLibraryAsync(cory);
        var (_, used) = await CreateInviteAsync(cory, library, name: "Used");
        var (_, revoked) = await CreateInviteAsync(cory, library, name: "Revoked");
        var (_, expired) = await CreateInviteAsync(cory, library, name: "Expired");
        var (ana, _) = await NewAccountAsync("Ana");
        (await AcceptAsync(ana, CodeOf(used))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var revokedId = revoked.GetProperty("invite").GetProperty("id").GetGuid();
        (await cory.SendAsync(HttpMethod.Delete, $"{InvitesUrl(library)}/{revokedId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var context = Db.CreateContext())
        {
            var expiredId = expired.GetProperty("invite").GetProperty("id").GetGuid();
            await context.LibraryInvites.IgnoreQueryFilters().Where(i => i.Id == expiredId)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.ExpiresAt, i => i.CreatedAt.AddSeconds(1)));
        }

        App.Time.Advance(TimeSpan.FromSeconds(2));

        var (sam, _) = await NewAccountAsync("Sam");
        var bad = new[]
        {
            "ZZZZ-ZZZZ-ZZ", CodeOf(used), CodeOf(revoked), CodeOf(expired),
            used.GetProperty("link").GetString()!, revoked.GetProperty("link").GetString()!, expired.GetProperty("link").GetString()!,
            "https://localhost/join/not-a-token", "not-a-token", string.Empty,
        };
        var bodies = new List<string>();
        foreach (var invite in bad)
        {
            foreach (var response in new[] { await PreviewAsync(sam, invite), await AcceptAsync(sam, invite) })
            {
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound, invite);
                bodies.Add(await BodyOfAsync(response));
            }
        }

        bodies.Distinct().Count().ShouldBe(1);
        (await LibraryIdsAsync(sam)).ShouldBeEmpty();
        (await CountMembersAsync(library)).ShouldBe(2);
    }

    [Fact]
    public async Task An_invite_that_expires_after_the_preview_cannot_be_accepted()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var library = await CreatedLibraryAsync(cory);
        var (_, created) = await CreateInviteAsync(cory, library);
        var (ana, _) = await NewAccountAsync("Ana");
        (await PreviewAsync(ana, CodeOf(created))).StatusCode.ShouldBe(HttpStatusCode.OK);

        App.Time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));

        // The phone's Auth0 token doesn't depend on the web session, which has long gone idle by now.
        var accepted = await App.NewPhone(ana.Identity.Subject).PostAsJsonAsync(AcceptUrl, new { invite = CodeOf(created) });
        await ShouldBeCodeAsync(accepted, HttpStatusCode.NotFound, "invite.invalid");
    }

    [Fact]
    public async Task A_member_who_accepts_another_invite_to_the_same_library_gets_409_and_the_invite_is_not_spent()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var library = await CreatedLibraryAsync(cory);
        var (_, first) = await CreateInviteAsync(cory, library, "viewer");
        var (_, second) = await CreateInviteAsync(cory, library, "editor");
        var (ana, account) = await NewAccountAsync("Ana");
        (await AcceptAsync(ana, CodeOf(first))).StatusCode.ShouldBe(HttpStatusCode.OK);

        await ShouldBeCodeAsync(await AcceptAsync(ana, CodeOf(second)), HttpStatusCode.Conflict, "invite.already_member");
        await ShouldBeCodeAsync(await AcceptAsync(cory, CodeOf(second)), HttpStatusCode.Conflict, "invite.already_member");

        (await RoleOfAsync(library, account.UserId)).ShouldBe("viewer");
        var (sam, _) = await NewAccountAsync("Sam");
        (await AcceptAsync(sam, CodeOf(second))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Preview_and_accept_need_a_signed_in_caller()
    {
        var stranger = App.NewBrowser();

        (await stranger.PostAsync(PreviewUrl, new { invite = "ABCD-EFGH-JK" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stranger.PostAsync(AcceptUrl, new { invite = "ABCD-EFGH-JK" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stranger.PostAsync(LibrariesUrl, new { name = "Mine" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>The same code as a person might type it: lower case, no dashes, spaces, and the look-alikes Crockford allows.</summary>
    private static string Messy(string code)
    {
        var typed = code.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant();
        return typed.Replace('0', 'o').Replace('1', 'l');
    }

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{2}$")]
    private static partial Regex CodeFormat();
}

/// <summary>Locked test 4 (limits): code lookups are rate-limited per account.</summary>
public class InviteLookupAccountLimitTests(ApiFixture api) : OnboardingTest(api)
{
    protected override Dictionary<string, string?>? MoreSettings => new() { ["Auth:RateLimit:AccountPermitLimit"] = "3" };

    [Fact]
    public async Task Code_guesses_are_limited_per_account()
    {
        var (ana, _) = await NewAccountAsync("Ana");
        for (var i = 0; i < 3; i++)
        {
            (await (i % 2 == 0 ? PreviewAsync(ana, "ZZZZ-ZZZZ-ZZ") : AcceptAsync(ana, "ZZZZ-ZZZZ-ZZ"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var refused = await PreviewAsync(ana, "ZZZZ-ZZZZ-Z0");

        await ShouldBeCodeAsync(refused, HttpStatusCode.TooManyRequests, "rate_limited");
        refused.Headers.RetryAfter.ShouldNotBeNull();
        await ShouldBeCodeAsync(await AcceptAsync(ana, "ZZZZ-ZZZZ-Z0"), HttpStatusCode.TooManyRequests, "rate_limited");
        await ShouldBeCodeAsync(await App.NewPhone(ana.Identity.Subject).PostAsJsonAsync(AcceptUrl, new { invite = "ZZZZ-ZZZZ-Z0" }), HttpStatusCode.TooManyRequests, "rate_limited");

        // Another account is not held back by Ana's guesses.
        var (sam, _) = await NewAccountAsync("Sam");
        (await PreviewAsync(sam, "ZZZZ-ZZZZ-Z0")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

/// <summary>Locked test 4 (limits): code lookups are rate-limited per client, whatever the account.</summary>
public class InviteLookupClientLimitTests(ApiFixture api) : OnboardingTest(api)
{
    protected override Dictionary<string, string?>? MoreSettings => new() { ["Auth:RateLimit:PermitLimit"] = "4" };

    [Fact]
    public async Task Code_guesses_are_limited_per_client_whatever_the_account()
    {
        // Two sign-ins take 2 of the client's 4 permits (GET /account/login); the guesses share the rest.
        var (ana, _) = await NewAccountAsync("Ana");
        var (sam, _) = await NewAccountAsync("Sam");
        (await PreviewAsync(ana, "ZZZZ-ZZZZ-ZZ")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AcceptAsync(sam, "ZZZZ-ZZZZ-ZZ")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await ShouldBeCodeAsync(await PreviewAsync(ana, "ZZZZ-ZZZZ-Z0"), HttpStatusCode.TooManyRequests, "rate_limited");
        await ShouldBeCodeAsync(await CreateLibraryAsync(sam), HttpStatusCode.TooManyRequests, "rate_limited");
    }
}

/// <summary>Locked test 5: removing a member or leaving ends that one membership on the next request; the account stays.</summary>
public class LeaveAndRemoveTests(ApiFixture api) : OnboardingTest(api)
{
    [Fact]
    public async Task Removing_a_member_ends_that_membership_on_the_next_request_and_keeps_the_account_and_its_other_libraries()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var corys = await CreatedLibraryAsync(cory, "Cory's library");
        var (ana, account) = await NewAccountAsync("Ana");
        var anas = await CreatedLibraryAsync(ana, "Ana's library");
        var (token, _) = await CreateInviteAsync(cory, corys, "editor");
        (await AcceptAsync(ana, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var phone = App.NewPhone(ana.Identity.Subject);
        (await phone.GetAsync(LibraryUrl(corys))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ana.GetAsync(LibraryUrl(corys))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await cory.SendAsync(HttpMethod.Delete, $"{MembersUrl(corys)}/{account.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldBeProblemAsync(await ana.GetAsync(LibraryUrl(corys)), HttpStatusCode.NotFound);
        await ShouldBeProblemAsync(await phone.GetAsync(LibraryUrl(corys)), HttpStatusCode.NotFound);
        (await ana.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LibraryIdsAsync(ana)).ShouldBe([anas]);
        (await ana.GetAsync(LibraryUrl(anas))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var context = Db.CreateContext();
        (await context.Users.SingleAsync(u => u.Id == account.UserId)).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_member_can_leave_and_loses_that_library_on_the_next_request_only()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var corys = await CreatedLibraryAsync(cory, "Cory's library");
        var (ana, account) = await NewAccountAsync("Ana");
        var anas = await CreatedLibraryAsync(ana, "Ana's library");
        var (token, _) = await CreateInviteAsync(cory, corys, "viewer");
        (await AcceptAsync(ana, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var phone = App.NewPhone(ana.Identity.Subject);

        var left = await phone.DeleteAsync(MembershipUrl(corys));

        left.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ShouldBeProblemAsync(await ana.GetAsync(LibraryUrl(corys)), HttpStatusCode.NotFound);
        await ShouldBeProblemAsync(await phone.GetAsync(LibraryUrl(corys)), HttpStatusCode.NotFound);
        (await LibraryIdsAsync(ana)).ShouldBe([anas]);
        (await CountMembersAsync(corys)).ShouldBe(1);
        (await ana.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var context = Db.CreateContext();
        (await context.Users.SingleAsync(u => u.Id == account.UserId)).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task The_last_owner_cannot_leave()
    {
        var (cory, account) = await NewAccountAsync("Cory");
        var corys = await CreatedLibraryAsync(cory);

        await ShouldBeCodeAsync(await cory.SendAsync(HttpMethod.Delete, MembershipUrl(corys), null), HttpStatusCode.Conflict, "library.last_owner");

        (await RoleOfAsync(corys, account.UserId)).ShouldBe("owner");
        (await cory.GetAsync(LibraryUrl(corys))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_removed_member_can_join_again_with_a_new_invite()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var corys = await CreatedLibraryAsync(cory);
        var (ana, account) = await NewAccountAsync("Ana");
        var (first, _) = await CreateInviteAsync(cory, corys, "editor");
        (await AcceptAsync(ana, first)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cory.SendAsync(HttpMethod.Delete, $"{MembersUrl(corys)}/{account.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var (second, _) = await CreateInviteAsync(cory, corys, "viewer");

        (await AcceptAsync(ana, second)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RoleOfAsync(corys, account.UserId)).ShouldBe("viewer");
    }
}

/// <summary>Locked test 6: the setup routes are gone; a recovery code links a new identity to the named account only.</summary>
public class SetupGoneAndRecoveryTests(ApiFixture api) : OnboardingTest(api)
{
    [Fact]
    public async Task The_setup_and_not_linked_routes_are_gone()
    {
        var routes = App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.ShouldNotContain("/setup");
        routes.ShouldNotContain("/account/not-linked");
        var browser = App.NewBrowser();
        (await browser.PostAsync("/setup", new { code = "AAAA-BBBB-CCCC-DDDD" })).IsSuccessStatusCode.ShouldBeFalse();
        (await browser.GetAsync("/setup")).IsSuccessStatusCode.ShouldBeFalse();
        (await browser.GetAsync("/account/not-linked")).IsSuccessStatusCode.ShouldBeFalse();
        (await CountRecoveryCodesAsync()).ShouldBe(0);
        App.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Select(s => s.GetType().Name).ShouldNotContain("SetupCodeBootstrapper");
        File.Exists(Path.Combine(App.SecretsPath, "setup-code.txt")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("intent=setup")]
    [InlineData("invite=some-token")]
    [InlineData("intent=join")]
    public async Task The_old_setup_and_join_intents_are_plain_sign_ins(string query)
    {
        var (cory, _) = await NewAccountAsync("Cory");
        await CreatedLibraryAsync(cory);
        var browser = App.NewBrowser();

        var response = await browser.SignInAsync("/account/login?" + query);

        response.Headers.Location?.ToString().ShouldBe("/");
        (await browser.MeAsync()).LibraryId.ShouldBeNull();
        (await CountOwnersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_recovery_code_links_a_new_identity_to_the_named_account_only()
    {
        var (cory, coryAccount) = await NewAccountAsync("Cory");
        var (_, anaAccount) = await NewAccountAsync("Ana");
        var (exit, code, _) = await RecoveryCodeForAsync(cory.Identity.Email!.ToUpperInvariant());
        exit.ShouldBe(0);
        var lostEverything = App.NewBrowser(TestIdentity.Apple("Cory"));

        var response = await lostEverything.ContinueAsync(await lostEverything.StartRecoveryAsync(code!));

        response.Headers.Location?.ToString().ShouldBe("/");
        (await lostEverything.MeAsync()).UserId.ShouldBe(coryAccount.UserId);
        await using var context = Db.CreateContext();
        (await context.Logins.CountAsync(l => l.UserId == coryAccount.UserId)).ShouldBe(2);
        (await context.Logins.CountAsync(l => l.UserId == anaAccount.UserId)).ShouldBe(1);
        (await CountUsersAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_recovery_code_never_takes_an_identity_from_another_account()
    {
        var (cory, coryAccount) = await NewAccountAsync("Cory");
        var (ana, anaAccount) = await NewAccountAsync("Ana");
        var (_, code, _) = await RecoveryCodeForAsync(cory.Identity.Email!);

        var thief = App.NewBrowser(ana.Identity);
        var response = await thief.ContinueAsync(await thief.StartRecoveryAsync(code!));

        response.Headers.Location?.ToString().ShouldBe("/?error=already_linked");
        thief.HasSessionCookie.ShouldBeFalse();
        await using var context = Db.CreateContext();
        (await context.Logins.SingleAsync(l => l.Subject == ana.Identity.Subject)).UserId.ShouldBe(anaAccount.UserId);
        (await context.Logins.CountAsync(l => l.UserId == coryAccount.UserId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_email_is_an_error_and_issues_nothing()
    {
        await NewAccountAsync("Cory");

        var (exit, code, error) = await RecoveryCodeForAsync("nobody@example.com");

        exit.ShouldNotBe(0);
        code.ShouldBeNull();
        error.ShouldNotBeEmpty();
        (await CountRecoveryCodesAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_recovery_code_works_once()
    {
        var (cory, _) = await NewAccountAsync("Cory");
        var (_, code, _) = await RecoveryCodeForAsync(cory.Identity.Email!);
        var first = App.NewBrowser(TestIdentity.Apple("Cory"));
        await first.ContinueAsync(await first.StartRecoveryAsync(code!));

        await ShouldBeProblemAsync(await App.NewBrowser().StartRecoveryAsync(code!), HttpStatusCode.Gone);
        (await CountLoginsAsync()).ShouldBe(2);
    }
}

/// <summary>Locked test 7: the authorization matrix covers "signed in, no libraries" and every new route.</summary>
public class OnboardingMatrixTests
{
    [Fact]
    public void The_matrix_has_a_signed_in_no_libraries_caller_and_a_row_for_every_new_route()
    {
        Enum.GetValues<Actor>().ShouldContain(Actor.NoLibraries);
        var rows = AuthorizationMatrix.Cases.ToDictionary(c => c.Name);

        rows["POST /api/v1/libraries"].ExpectedFor(Actor.NoLibraries).ShouldBe(201);
        rows["POST /api/v1/libraries"].ExpectedFor(Actor.Anonymous).ShouldBe(401);
        rows["POST /api/v1/invites:preview"].ExpectedFor(Actor.NoLibraries).ShouldBe(404);
        rows["POST /api/v1/invites:preview"].ExpectedFor(Actor.Anonymous).ShouldBe(401);
        rows["POST /api/v1/invites:accept"].ExpectedFor(Actor.NoLibraries).ShouldBe(404);
        rows["POST /api/v1/invites:accept"].ExpectedFor(Actor.Anonymous).ShouldBe(401);
        rows["GET /api/v1/libraries"].ExpectedFor(Actor.NoLibraries).ShouldBe(200);
        rows["GET /api/v1/me"].ExpectedFor(Actor.NoLibraries).ShouldBe(200);

        var leave = rows["DELETE /api/v1/libraries/{libraryId:guid}/membership"];
        (leave.ExpectedFor(Actor.Anonymous), leave.ExpectedFor(Actor.NoLibraries), leave.ExpectedFor(Actor.OtherUser)).ShouldBe((401, 404, 404));
        (leave.ExpectedFor(Actor.Viewer), leave.ExpectedFor(Actor.Editor), leave.ExpectedFor(Actor.Owner)).ShouldBe((204, 204, 409));

        // Every library route answers a caller with no libraries 404, like any other non-member.
        foreach (var row in AuthorizationMatrix.Cases.Where(c => c.Route.StartsWith("/api/v1/libraries/{libraryId", StringComparison.Ordinal)))
        {
            row.ExpectedFor(Actor.NoLibraries).ShouldBe(404, row.Name);
        }

        rows.Keys.ShouldNotContain("GET /setup");
        rows.Keys.ShouldNotContain("POST /setup");
        rows.Keys.ShouldNotContain("GET /account/not-linked");
    }
}
