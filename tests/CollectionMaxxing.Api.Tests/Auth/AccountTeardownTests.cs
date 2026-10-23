using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Accounts.IdentityProvider;
using CollectionMaxxing.Domain.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance tests of AUTH-2 (issue #229), member removal and the purge reach Auth0. Do not edit, weaken, skip or delete: add new tests instead.
// ONB-1 (#249, docs/15): removing a member now ends only that membership. The person keeps their account, their sign-ins and their other
// libraries, so removal no longer signs them out at Auth0 or sets a token cutoff; it locks them out of that library from their next call
// because memberships are read on every request. The two removal tests below say so. Deleting an account still reaches Auth0.
public class AccountTeardownTests(ApiFixture api) : ManagementTest(api)
{
    [Fact]
    public async Task Removing_a_member_locks_their_token_out_of_that_library_at_once_whatever_auth0_says()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (member, memberAccount) = await AddMemberAsync(owner, libraryId, "editor");
        var token = EarlierToken(member.Identity.Subject);
        (await CallAsync(HttpMethod.Get, $"/api/v1/libraries/{libraryId}", token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Management.Failing(500);

        var removed = await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", null);

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, "an Auth0 outage never blocks removing a member");
        (await CallAsync(HttpMethod.Get, $"/api/v1/libraries/{libraryId}", token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CallAsync(HttpMethod.Get, $"/api/v1/libraries/{libraryId}/items", App.Auth0.AccessToken(member.Identity.Subject))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CallAsync(HttpMethod.Get, "/api/v1/me", token)).StatusCode.ShouldBe(HttpStatusCode.OK, "the account stays");
        (await TokensNotBeforeAsync(memberAccount.UserId)).ShouldBeNull("nobody was signed out");
        (await TokensNotBeforeAsync(account.UserId)).ShouldBeNull("the owner was not signed out");
    }

    [Fact]
    public async Task Removing_a_member_asks_auth0_for_nothing_and_signs_nobody_out()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (_, memberAccount) = await AddMemberAsync(owner, libraryId, "viewer");

        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Management.GrantRevocations.ShouldBeEmpty("only the membership ends");
        (await JobsAsync(RevokeIdentitySessions.JobType)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_an_account_ends_the_refresh_tokens_at_once_and_the_purge_deletes_the_auth0_user_and_a_second_run_does_nothing()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var subject = owner.Identity.Subject;
        var token = EarlierToken(subject);

        (await DeleteAccountAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Management.GrantRevocations.ShouldHaveSingleItem().Url.ShouldContain("user_id=" + subject);
        (await TokensNotBeforeAsync(account.UserId)).ShouldNotBeNull();
        (await CallAsync(HttpMethod.Get, "/api/v1/me", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        Management.UserDeletions.ShouldBeEmpty("the Auth0 user stays for the 30 days");

        App.Time.Advance(PurgeAccount.Retention + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        Management.UserDeletions.ShouldHaveSingleItem().Url.ShouldContain("/api/v2/users/" + subject);
        (await CountLoginsAsync()).ShouldBe(0, "the purge erased the linked identity after Auth0 had deleted it");

        await using (var scope = App.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PurgeAccount.Handler>().HandleAsync(new PurgeAccountPayload(account.UserId), CancellationToken.None);
        }

        Management.UserDeletions.Count.ShouldBe(1, "a second purge finds nothing left to delete");
    }

    [Fact]
    public async Task A_purge_that_auth0_refuses_keeps_the_identity_and_finishes_when_auth0_recovers()
    {
        var (owner, account) = await SetUpOwnerAsync();
        (await DeleteAccountAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Management.Failing(500);

        App.Time.Advance(PurgeAccount.Retention + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        Management.UserDeletions.ShouldNotBeEmpty("Auth0 was asked");
        (await CountLoginsAsync()).ShouldBe(1, "the identity is kept, so the retry knows whom to delete");
        (await PurgeJobsAsync()).ShouldAllBe(job => job.Status != JobStatus.Succeeded);

        Management.Healthy();
        App.Time.Advance(TimeSpan.FromDays(1));
        await RunDueJobsAsync();

        Management.UserDeletions[^1].Url.ShouldContain("/api/v2/users/" + owner.Identity.Subject);
        (await CountLoginsAsync()).ShouldBe(0);
        (await UserRowAsync(account.UserId)).DisplayName.ShouldBe(PurgeAccount.RemovedUserName);
    }

    [Fact]
    public async Task A_purge_of_a_user_auth0_has_already_deleted_is_done()
    {
        var (owner, _) = await SetUpOwnerAsync();
        (await DeleteAccountAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Management.Gone();

        App.Time.Advance(PurgeAccount.Retention + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        (await CountLoginsAsync()).ShouldBe(0);
        (await PurgeJobsAsync()).ShouldHaveSingleItem().Status.ShouldBe(JobStatus.Succeeded);
    }
}
