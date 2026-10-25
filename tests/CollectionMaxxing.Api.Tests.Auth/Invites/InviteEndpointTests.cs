using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Invites;

// LOCKED acceptance tests of ID-3a (issue #64), creating invites and the invite preview. Do not edit, weaken, skip or delete: add new tests instead.
public class InviteCreationTests(ApiFixture api) : InviteTest(api)
{
    [Fact]
    public async Task Creating_an_invite_needs_a_passkey_sign_in_from_the_last_5_minutes()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        App.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var stale = await InviteAsync(owner, libraryId);

        await ShouldBeProblemAsync(stale, HttpStatusCode.Unauthorized);
        var problem = await ReadAsync(stale);
        problem.GetProperty("type").GetString().ShouldBe("https://collectionmaxxing.app/problems/reauth_required");
        problem.GetProperty("reauth").GetBoolean().ShouldBeTrue();
        (await owner.GetAsync(InvitesUrl(libraryId))).StatusCode.ShouldBe(HttpStatusCode.OK); // listing needs no re-auth

        (await owner.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        (await InviteAsync(owner, libraryId)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_new_invite_returns_the_link_and_qr_payload_once_and_lasts_seven_days()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var now = App.Time.GetUtcNow();

        var (token, created) = await CreateInviteAsync(owner, libraryId, "editor", "Sam");

        token.Length.ShouldBeGreaterThanOrEqualTo(40);
        created.GetProperty("link").GetString().ShouldBe($"{PublicUrl}/join/{token}");
        created.GetProperty("qrPayload").GetString().ShouldBe(created.GetProperty("link").GetString());
        var invite = created.GetProperty("invite");
        invite.GetProperty("name").GetString().ShouldBe("Sam");
        invite.GetProperty("role").GetString().ShouldBe("editor");
        invite.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(now + TimeSpan.FromDays(7), TimeSpan.FromSeconds(1));
        invite.GetProperty("acceptedAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        var list = await ReadAsync(await owner.GetAsync(InvitesUrl(libraryId)));
        var listed = list.GetProperty("items").EnumerateArray().Single();
        listed.GetProperty("id").GetGuid().ShouldBe(invite.GetProperty("id").GetGuid());
        listed.TryGetProperty("link", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{ "role": "viewer" }""")]
    [InlineData("""{ "name": "  ", "role": "viewer" }""")]
    [InlineData("""{ "name": "Sam" }""")]
    public async Task An_invite_needs_a_name_and_a_role(string json)
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();

        var response = await owner.PostAsync(InvitesUrl(ownerAccount.LibraryId!.Value), System.Text.Json.JsonDocument.Parse(json).RootElement);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await BodyOfAsync(response));
    }

    [Fact]
    public async Task The_owner_lists_pending_accepted_and_revoked_invites()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (acceptedToken, _) = await CreateInviteAsync(owner, libraryId, "viewer", "Accepted");
        var (_, revoked) = await CreateInviteAsync(owner, libraryId, "viewer", "Revoked");
        await CreateInviteAsync(owner, libraryId, "editor", "Pending");
        (await JoinAsync(acceptedToken, "Accepted")).Registered.Headers.Location?.ToString().ShouldBe("/");
        (await owner.SendAsync(HttpMethod.Delete, $"{InvitesUrl(libraryId)}/{revoked.GetProperty("invite").GetProperty("id").GetGuid()}", null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var items = (await ReadAsync(await owner.GetAsync(InvitesUrl(libraryId)))).GetProperty("items").EnumerateArray().ToArray();

        items.Length.ShouldBe(3);
        var byName = items.ToDictionary(i => i.GetProperty("name").GetString()!);
        byName["Accepted"].GetProperty("acceptedBy").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
        byName["Revoked"].GetProperty("revokedAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
        byName["Pending"].GetProperty("acceptedAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        byName["Pending"].GetProperty("revokedAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Revoking_twice_is_fine_but_an_accepted_invite_cannot_be_revoked()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (_, pending) = await CreateInviteAsync(owner, libraryId, "viewer", "Pending");
        var (acceptedToken, accepted) = await CreateInviteAsync(owner, libraryId, "viewer", "Accepted");
        (await JoinAsync(acceptedToken)).Registered.Headers.Location?.ToString().ShouldBe("/");
        string Url(System.Text.Json.JsonElement created) => $"{InvitesUrl(libraryId)}/{created.GetProperty("invite").GetProperty("id").GetGuid()}";

        (await owner.SendAsync(HttpMethod.Delete, Url(pending), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.SendAsync(HttpMethod.Delete, Url(pending), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.SendAsync(HttpMethod.Delete, Url(accepted), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await owner.SendAsync(HttpMethod.Delete, $"{InvitesUrl(libraryId)}/{Guid.NewGuid()}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_preview_tells_the_join_page_who_invited_them_and_to_what()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (token, _) = await CreateInviteAsync(owner, ownerAccount.LibraryId!.Value, "editor", "Sam");
        var now = App.Time.GetUtcNow();

        var response = await App.NewBrowser().GetAsync($"/api/v1/invites/{token}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await BodyOfAsync(response));
        var preview = await ReadAsync(response);
        preview.GetProperty("ownerName").GetString().ShouldBe("Owner");
        preview.GetProperty("libraryName").GetString().ShouldBe("Owner's library");
        preview.GetProperty("role").GetString().ShouldBe("editor");
        preview.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(now + TimeSpan.FromDays(7), TimeSpan.FromSeconds(1));
        preview.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["expiresAt", "libraryName", "ownerName", "role"]); // nothing else about the invite is shown
    }

    [Fact]
    public async Task Joining_keeps_the_name_the_account_got_from_its_sign_in()
    {
        // ONB-1 (docs/15): the account exists before it joins, named from Google or Apple; the invite's name is only the owner's label.
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "viewer", "Sam");

        var (_, registered) = await JoinAsync(token, "Samantha");

        registered.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListMembersAsync(owner, libraryId)).Select(m => m.GetProperty("displayName").GetString()).Order().ShouldBe(["Owner", "Samantha"]);
    }

    [Fact]
    public async Task A_member_who_joined_can_use_the_library_straight_away_and_pair_their_own_phone()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (sam, samAccount) = await AddMemberAsync(owner, libraryId, "viewer");

        (await sam.GetAsync($"/api/v1/libraries/{libraryId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await ReadAsync(await sam.GetAsync("/api/v1/me"));
        me.GetProperty("id").GetGuid().ShouldBe(samAccount.UserId);
        var phone = await SignInPhoneAsync(sam, "Sam's iPhone");
        (await AsDeviceAsync(HttpMethod.Get, $"/api/v1/libraries/{libraryId}", phone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The owner's sign-in methods are not Sam's, and Sam is not an administrator.
        (await ReadAsync(await owner.GetAsync("/api/v1/me/sign-in-methods"))).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await sam.GetAsync("/api/admin/jobs")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

// LOCKED acceptance tests of ID-3a (issue #64): the preview endpoint is rate-limited and reveals nothing. Do not edit, weaken, skip or delete.
public class InvitePreviewLimitTests(ApiFixture api) : InviteTest(api)
{
    protected override Dictionary<string, string?>? MoreSettings => new() { ["Auth:RateLimit:PermitLimit"] = "15", ["Auth:RateLimit:WindowSeconds"] = "600" };

    [Fact]
    public async Task Guessing_tokens_on_the_preview_endpoint_is_rate_limited_with_retry_after()
    {
        var guesser = App.NewBrowser();
        HttpResponseMessage? limited = null;

        for (var guess = 0; guess < 40 && limited is null; guess++)
        {
            var response = await guesser.GetAsync($"/api/v1/invites/guess-{guess}");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
            else
            {
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }
        }

        limited.ShouldNotBeNull("40 guesses in a row should have hit the limit");
        await ShouldBeProblemAsync(limited, HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }
}

// LOCKED acceptance test of ID-3a (issue #64): no invite token or hash appears in logs. Do not edit, weaken, skip or delete.
public class InviteLogHygieneTests(ApiFixture api) : InviteTest(api)
{
    [Fact]
    public async Task No_invite_token_or_hash_appears_in_the_logs()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var secrets = new List<string>();

        // A token that is used, one that is revoked, one that expires, and one that never existed: every route a token travels.
        var (used, _) = await CreateInviteAsync(owner, libraryId, "editor", "Used");
        var (revoked, revokedInvite) = await CreateInviteAsync(owner, libraryId, "viewer", "Revoked");
        var (expiring, _) = await CreateInviteAsync(owner, libraryId, "viewer", "Expiring");
        var unknown = "unknown-" + Guid.NewGuid().ToString("N");
        secrets.AddRange([used, revoked, expiring, unknown]);

        var guest = App.NewBrowser();
        (await guest.GetAsync($"/join/{used}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await guest.GetAsync($"/api/v1/invites/{used}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JoinAsync(used)).Registered.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await guest.GetAsync($"/api/v1/invites/{used}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await guest.GetAsync($"/join/{unknown}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await guest.GetAsync($"/api/v1/invites/{unknown}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await guest.JoinAsync(unknown)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await guest.PostAsync("/api/v1/invites:preview", new { invite = unknown })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await guest.GetAsync($"/signin-oidc?code={unknown}&state={unknown}")).Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");

        (await owner.SendAsync(HttpMethod.Delete, $"{InvitesUrl(libraryId)}/{revokedInvite.GetProperty("invite").GetProperty("id").GetGuid()}", null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await guest.JoinAsync(revoked)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        App.Time.Advance(TimeSpan.FromDays(8));
        guest.ClearCookies();
        (await guest.JoinAsync(expiring)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await guest.GetAsync($"/api/v1/invites/{expiring}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        owner.ClearCookies();
        (await owner.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        (await owner.GetAsync(InvitesUrl(libraryId))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await owner.GetAsync(MembersUrl(libraryId));

        // The hash is a secret too (anyone holding it can't join, but it is as good as a fingerprint of the link).
        secrets.AddRange(secrets.ToArray().Select(Sha256Hex));

        App.Logs.Lines.Count.ShouldBeGreaterThan(0, "the capture should see the app's log lines");
        foreach (var secret in secrets)
        {
            App.Logs.Lines.Where(line => line.Contains(secret, StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty($"a log line contains '{secret[..8]}…'");
        }
    }
}
