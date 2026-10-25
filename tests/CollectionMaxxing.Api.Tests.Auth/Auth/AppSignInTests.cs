using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts.IdentityProvider;
using CollectionMaxxing.Domain.Operations;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance tests of AUTH-2 (issue #229), iPhone sign-ins and signing them out. Do not edit, weaken, skip or delete: add new tests instead.
public class AppSignInTests(ApiFixture api) : ManagementTest(api)
{
    [Fact]
    public async Task After_the_iphones_are_signed_out_a_token_that_was_valid_a_moment_ago_gets_401()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var subject = owner.Identity.Subject;
        var token = EarlierToken(subject);
        (await CallAsync(HttpMethod.Get, "/api/v1/me", token)).StatusCode.ShouldBe(HttpStatusCode.OK, "the token is good before the sign-out");
        (await RegisterAsync(token, Guid.NewGuid())).StatusCode.ShouldBe(HttpStatusCode.Created);

        var signedOut = await CallAsync(HttpMethod.Delete, AppSignIns, token);

        signedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent, await signedOut.Content.ReadAsStringAsync());
        await ShouldBeProblemAsync(await CallAsync(HttpMethod.Get, "/api/v1/me", token), HttpStatusCode.Unauthorized);
        (await TokensNotBeforeAsync(account.UserId)).ShouldNotBeNull();
        (await CountAppSignInsAsync(account.UserId)).ShouldBe(0, "the phones are forgotten");
    }

    [Fact]
    public async Task Signing_the_iphones_out_ends_their_auth0_refresh_tokens_with_the_management_token()
    {
        var (owner, _) = await SetUpOwnerAsync();
        (await CallAsync(HttpMethod.Delete, AppSignIns, EarlierToken(owner.Identity.Subject))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var revoked = Management.GrantRevocations.ShouldHaveSingleItem();
        revoked.Url.ShouldContain("user_id=" + owner.Identity.Subject);
        revoked.Authorization.ShouldBe("Bearer m2m-token");
        Management.TokenRequests.ShouldHaveSingleItem().Body.ShouldNotBeNull().ShouldContain("client_credentials");
        Management.UserDeletions.ShouldBeEmpty("signing out never deletes the Auth0 user");
    }

    [Fact]
    public async Task A_sign_in_made_in_the_same_second_as_the_sign_out_works_at_once_and_one_from_the_second_before_does_not()
    {
        var (owner, _) = await SetUpOwnerAsync();
        var subject = owner.Identity.Subject;
        var before = EarlierToken(subject);

        // Stand a little way into a second, so the sign-out's moment has a fraction that the token's iat (whole seconds) has not.
        var now = App.Time.GetUtcNow();
        App.Time.Advance(DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()).AddSeconds(1).AddMilliseconds(900) - now);
        var signOutAt = App.Time.GetUtcNow();
        (await CallAsync(HttpMethod.Delete, AppSignIns, before)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var sameSecond = TokenIssuedAt(subject, signOutAt);
        var secondBefore = TokenIssuedAt(subject, DateTimeOffset.FromUnixTimeSeconds(signOutAt.ToUnixTimeSeconds() - 1));

        (await CallAsync(HttpMethod.Get, "/api/v1/me", sameSecond)).StatusCode.ShouldBe(HttpStatusCode.OK, "a new sign-in right after a sign-out is not locked out");
        (await CallAsync(HttpMethod.Get, "/api/v1/me", secondBefore)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(HttpMethod.Get, "/api/v1/me", before)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signing_out_one_person_does_not_touch_anyone_elses_tokens()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (member, _) = await AddMemberAsync(owner, account.LibraryId!.Value, "viewer");
        var memberToken = EarlierToken(member.Identity.Subject);

        (await CallAsync(HttpMethod.Delete, AppSignIns, EarlierToken(owner.Identity.Subject))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await CallAsync(HttpMethod.Get, "/api/v1/me", memberToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_auth0_fails_the_sign_out_still_works_and_a_retry_finishes_the_revoke()
    {
        var (owner, _) = await SetUpOwnerAsync();
        var token = EarlierToken(owner.Identity.Subject);
        Management.Failing(500);

        var signedOut = await CallAsync(HttpMethod.Delete, AppSignIns, token);

        signedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent, "an Auth0 outage never blocks the sign-out");
        (await CallAsync(HttpMethod.Get, "/api/v1/me", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the cutoff already locks the phones out");
        (await JobsAsync(RevokeIdentitySessions.JobType)).ShouldHaveSingleItem().Status.ShouldBe(JobStatus.Pending);

        Management.Healthy();
        await RunDueJobsAsync();

        (await JobsAsync(RevokeIdentitySessions.JobType)).ShouldHaveSingleItem().Status.ShouldBe(JobStatus.Succeeded);
        Management.GrantRevocations.Count.ShouldBe(2, "one failed try and the retry");
    }

    [Fact]
    public async Task An_identity_auth0_no_longer_knows_counts_as_revoked_and_nothing_is_retried()
    {
        var (owner, _) = await SetUpOwnerAsync();
        Management.Gone();

        (await CallAsync(HttpMethod.Delete, AppSignIns, EarlierToken(owner.Identity.Subject))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await JobsAsync(RevokeIdentitySessions.JobType)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_management_token_is_kept_until_five_minutes_before_it_expires_and_then_renewed()
    {
        var (owner, _) = await SetUpOwnerAsync(); // Management.TokenLifetimeSeconds is 3600

        // The browser's cookie signs the phones out, so these calls do not depend on how old a bearer token is.
        async Task SignOutPhonesAsync() =>
            (await owner.SendAsync(HttpMethod.Delete, AppSignIns, null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await SignOutPhonesAsync();
        App.Time.Advance(TimeSpan.FromMinutes(54));
        await SignOutPhonesAsync();
        Management.TokenRequests.Count.ShouldBe(1, "54 minutes in, the token has more than five minutes left");

        App.Time.Advance(TimeSpan.FromMinutes(2));
        await SignOutPhonesAsync();
        Management.TokenRequests.Count.ShouldBe(2, "56 minutes in, less than five minutes are left, so the token is renewed");
        Management.GrantRevocations.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_browser_can_sign_the_iphones_out_too_but_only_with_the_csrf_header_and_it_stays_signed_in()
    {
        var (owner, _) = await SetUpOwnerAsync();
        var token = EarlierToken(owner.Identity.Subject);

        (await owner.SendAsync(HttpMethod.Delete, AppSignIns, null, csrf: false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CallAsync(HttpMethod.Get, "/api/v1/me", token)).StatusCode.ShouldBe(HttpStatusCode.OK, "the refused request signed nothing out");

        (await owner.SendAsync(HttpMethod.Delete, AppSignIns, null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await CallAsync(HttpMethod.Get, "/api/v1/me", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await owner.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK, "the web session is not a phone");
    }
}
