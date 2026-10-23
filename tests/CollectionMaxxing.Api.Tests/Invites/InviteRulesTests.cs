using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CollectionMaxxing.Api.Tests.Invites;

// LOCKED acceptance tests of ID-3a (issue #64), the rules of an invite. Do not edit, weaken, skip or delete: add new tests instead.
// AUTH-1 (#228): "passkey options" became the trip to Auth0 (a 302), and the refusal is the redirect to /login?error=invite_invalid.
public class InviteRulesTests(ApiFixture api) : InviteTest(api)
{
    [Fact]
    public async Task An_invite_works_once_a_second_registration_with_the_same_token_fails()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "editor");

        // Both people open the link and get passkey options while the invite is still unused.
        var first = App.NewBrowser();
        var second = App.NewBrowser();
        var firstOptions = await StartJoinAsync(first, token, "First");
        var secondOptions = await StartJoinAsync(second, token, "Second");
        firstOptions.StatusCode.ShouldBe(HttpStatusCode.Found);
        secondOptions.StatusCode.ShouldBe(HttpStatusCode.Found);

        var won = await first.FinishAsync(firstOptions);
        var lost = await second.FinishAsync(secondOptions);

        won.Headers.Location?.ToString().ShouldBe("/");
        lost.Headers.Location?.ToString().ShouldBe(InviteInvalid);
        second.HasSessionCookie.ShouldBeFalse();
        (await CountMembersAsync(libraryId)).ShouldBe(2);
        (await CountUsersAsync()).ShouldBe(2);

        // And the link is dead for anyone who opens it later.
        (await StartJoinAsync(App.NewBrowser(), token)).Headers.Location?.ToString().ShouldBe(InviteInvalid);
    }

    [Fact]
    public async Task Two_concurrent_registrations_with_the_same_token_let_exactly_one_win()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;

        for (var round = 0; round < 5; round++)
        {
            var (token, _) = await CreateInviteAsync(owner, libraryId, "viewer", $"Guest {round}");
            var one = App.NewBrowser();
            var two = App.NewBrowser();
            var oneOptions = await StartJoinAsync(one, token, "One");
            var twoOptions = await StartJoinAsync(two, token, "Two");

            var results = await Task.WhenAll(one.FinishAsync(oneOptions), two.FinishAsync(twoOptions));

            results.Count(r => r.Headers.Location?.ToString() == "/").ShouldBe(1, $"round {round}");
            results.Count(r => r.Headers.Location?.ToString() == InviteInvalid).ShouldBe(1, $"round {round}");
            (await CountMembersAsync(libraryId)).ShouldBe(2 + round, $"round {round}");
        }
    }

    [Fact]
    public async Task An_invite_works_six_days_23_hours_59_minutes_59_seconds_after_it_was_made()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (token, _) = await CreateInviteAsync(owner, ownerAccount.LibraryId!.Value, "viewer");
        App.Time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));

        var (_, registered) = await JoinAsync(token);

        registered.Headers.Location?.ToString().ShouldBe("/");
    }

    [Fact]
    public async Task An_invite_7_days_and_1_second_old_cannot_be_used()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "viewer");
        App.Time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));

        var response = await StartJoinAsync(App.NewBrowser(), token);

        response.Headers.Location?.ToString().ShouldBe(InviteInvalid);
        (await CountMembersAsync(libraryId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_invite_that_expires_while_the_person_is_creating_their_passkey_is_refused()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "viewer");
        App.Time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        var guest = App.NewBrowser();
        var options = await StartJoinAsync(guest, token);
        options.StatusCode.ShouldBe(HttpStatusCode.Found);
        App.Time.Advance(TimeSpan.FromMinutes(2));

        var registered = await guest.FinishAsync(options);

        registered.Headers.Location?.ToString().ShouldBe(InviteInvalid);
        (await CountMembersAsync(libraryId)).ShouldBe(1);
        (await CountUsersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_revoked_invite_cannot_be_used_even_by_someone_who_already_has_passkey_options()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (token, created) = await CreateInviteAsync(owner, libraryId, "viewer");
        var guest = App.NewBrowser();
        var options = await StartJoinAsync(guest, token);
        options.StatusCode.ShouldBe(HttpStatusCode.Found);

        var revoke = await owner.SendAsync(HttpMethod.Delete, $"{InvitesUrl(libraryId)}/{created.GetProperty("invite").GetProperty("id").GetGuid()}", null);
        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await guest.FinishAsync(options)).Headers.Location?.ToString().ShouldBe(InviteInvalid);
        (await StartJoinAsync(App.NewBrowser(), token)).Headers.Location?.ToString().ShouldBe(InviteInvalid);
        (await CountMembersAsync(libraryId)).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_expired_revoked_and_used_tokens_all_get_the_same_response()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (expired, _) = await CreateInviteAsync(owner, libraryId, "viewer", "Expired");
        var (revoked, revokedInvite) = await CreateInviteAsync(owner, libraryId, "viewer", "Revoked");
        var (used, _) = await CreateInviteAsync(owner, libraryId, "viewer", "Used");
        (await owner.SendAsync(HttpMethod.Delete, $"{InvitesUrl(libraryId)}/{revokedInvite.GetProperty("invite").GetProperty("id").GetGuid()}", null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await JoinAsync(used)).Registered.Headers.Location?.ToString().ShouldBe("/");
        App.Time.Advance(TimeSpan.FromDays(8));
        owner.ClearCookies();
        (await owner.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        var (fresh, _) = await CreateInviteAsync(owner, libraryId, "viewer", "Fresh");
        (await JoinAsync(fresh)).Registered.Headers.Location?.ToString().ShouldBe("/"); // sanity: a good one still works

        var tokens = new[] { "no-such-token", Guid.NewGuid().ToString("N"), expired, revoked, used };
        var starts = new List<(HttpStatusCode Status, string Body)>();
        var previews = new List<(HttpStatusCode Status, string Body)>();
        foreach (var token in tokens)
        {
            var start = await StartJoinAsync(App.NewBrowser(), token);
            starts.Add((start.StatusCode, start.Headers.Location?.ToString() + await BodyOfAsync(start)));
            var preview = await App.NewBrowser().GetAsync($"/api/v1/invites/{token}");
            previews.Add((preview.StatusCode, await BodyOfAsync(preview)));
        }

        starts.Select(s => s.Status).Distinct().ShouldBe([HttpStatusCode.Found]);
        starts.Select(s => s.Body).Distinct().ShouldBe([InviteInvalid]);
        starts.Select(s => s.Body).Distinct().Count().ShouldBe(1, "the body must not depend on why the token failed");
        previews.Select(s => s.Status).Distinct().ShouldBe([HttpStatusCode.NotFound]);
        previews.Select(s => s.Body).Distinct().Count().ShouldBe(1, "the body must not depend on why the token failed");
    }

    [Fact]
    public async Task The_token_is_stored_only_as_a_hash()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (token, created) = await CreateInviteAsync(owner, ownerAccount.LibraryId!.Value, "editor");

        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select to_jsonb(i)::text from library_invites i", connection);
        var rows = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0));
            }
        }

        var row = rows.ShouldHaveSingleItem();
        row.ShouldNotContain(token);
        await using var context = Db.CreateContext();
        var stored = await context.LibraryInvites.IgnoreQueryFilters().SingleAsync();
        stored.TokenHash.ShouldNotBe(token);
        stored.TokenHash.ShouldBe(Sha256Hex(token), "a plain SHA-256 of the token, as for pairing tokens");

        // The list shows invites without any way to get the link back.
        var list = await owner.GetAsync(InvitesUrl(ownerAccount.LibraryId!.Value));
        (await BodyOfAsync(list)).ShouldNotContain(token);
        created.GetProperty("link").GetString()!.ShouldEndWith("/join/" + token);
    }

    [Fact]
    public async Task Nobody_gets_a_membership_without_a_valid_invite()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var users = await CountUsersAsync();
        var members = await CountMembersAsync(libraryId);

        // No sign-in at all, or a sign-in with no invite: nothing.
        var stranger = App.NewBrowser();
        (await stranger.GetAsync("/signin-oidc?code=made-up&state=made-up")).Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");
        (await stranger.SignInAsync()).Headers.Location?.ToString().ShouldBe("/account/not-linked");

        // A token that doesn't exist starts nothing.
        (await StartJoinAsync(stranger, "made-up")).Headers.Location?.ToString().ShouldBe(InviteInvalid);

        // An empty token is not an invite either.
        (await stranger.SignInAsync("/account/login?invite=")).Headers.Location?.ToString().ShouldBe("/account/not-linked");

        // An invite's token in the callback's address is not an invite: only the sealed intent counts.
        var (token, _) = await CreateInviteAsync(owner, libraryId, "viewer");
        var started = await stranger.GetAsync("/account/login");
        stranger.Challenge(started).ShouldNotBeNull();
        (await stranger.FinishAsync(started)).Headers.Location?.ToString().ShouldBe("/account/not-linked");
        (await stranger.GetAsync($"/signin-oidc?invite={token}&code=x&state=y")).Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");

        (await CountUsersAsync()).ShouldBe(users);
        (await CountMembersAsync(libraryId)).ShouldBe(members);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("editor")]
    public async Task The_member_gets_exactly_the_invited_role(string role)
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;

        var (_, member) = await AddMemberAsync(owner, libraryId, role);

        member.LibraryId.ShouldBe(libraryId);
        (await RoleOfAsync(libraryId, member.UserId)).ShouldBe(role);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("Owner")]
    public async Task An_invite_can_never_grant_owner(string role)
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;

        var response = await owner.PostAsync(InvitesUrl(libraryId), new { name = "Sam", role });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await BodyOfAsync(response));
        await using var context = Db.CreateContext();
        (await context.LibraryInvites.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await CountOwnersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_database_itself_refuses_an_invite_for_an_owner()
    {
        var (_, ownerAccount) = await SetUpOwnerAsync();
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "insert into library_invites (id, library_id, role, name, token_hash, created_by, created_at, expires_at) "
            + "values (@id, @lib, 'owner', 'x', 'h', @user, now(), now() + interval '1 day')",
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("lib", ownerAccount.LibraryId!.Value);
        command.Parameters.AddWithValue("user", ownerAccount.UserId);

        var failure = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        failure.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }
}
