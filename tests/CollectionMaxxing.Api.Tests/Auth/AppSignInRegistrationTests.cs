using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Auth;

// AUTH-2 (issue #229), the contract addendum: POST/GET /api/v1/me/app-sign-ins.
public class AppSignInRegistrationTests(ApiFixture api) : ManagementTest(api)
{
    [Fact]
    public async Task A_phone_registers_with_201_then_updates_the_same_row_with_200_and_the_list_shows_it()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var token = EarlierToken(owner.Identity.Subject);
        var install = Guid.NewGuid();

        var created = await RegisterAsync(token, install, "Cory's iPhone");
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var first = await ReadAsync(created);
        first.GetProperty("deviceName").GetString().ShouldBe("Cory's iPhone");
        first.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        first.GetProperty("createdAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UnixEpoch);

        App.Time.Advance(TimeSpan.FromHours(2));
        var updated = await RegisterAsync(App.Auth0.AccessToken(owner.Identity.Subject), install, "Cory's new iPhone name");

        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await ReadAsync(updated);
        second.GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());
        second.GetProperty("deviceName").GetString().ShouldBe("Cory's new iPhone name");
        second.GetProperty("lastSeenAt").GetDateTimeOffset().ShouldBeGreaterThan(first.GetProperty("lastSeenAt").GetDateTimeOffset());
        (await CountAppSignInsAsync(account.UserId)).ShouldBe(1);

        (await RegisterAsync(App.Auth0.AccessToken(owner.Identity.Subject), Guid.NewGuid(), "iPad")).StatusCode.ShouldBe(HttpStatusCode.Created);
        var listed = await owner.GetAsync(AppSignIns); // the web app lists them with its cookie
        listed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await ReadAsync(listed);
        page.GetProperty("items").GetArrayLength().ShouldBe(2);
        page.GetProperty("nextCursor").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        page.GetProperty("items")[0].GetProperty("deviceName").GetString().ShouldBe("iPad", "the most recently seen phone comes first");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    public async Task A_missing_or_malformed_install_id_is_400(string? header)
    {
        var (owner, _) = await SetUpOwnerAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, AppSignIns)
        {
            Content = new StringContent("""{ "deviceName": "iPhone" }""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", App.Auth0.AccessToken(owner.Identity.Subject));
        if (header is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Install-Id", header);
        }

        await ShouldBeProblemAsync(await App.NewBrowser().Http.SendAsync(request), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_phone_without_a_name_is_400(string name)
    {
        var (owner, _) = await SetUpOwnerAsync();

        await ShouldBeProblemAsync(await RegisterAsync(App.Auth0.AccessToken(owner.Identity.Subject), Guid.NewGuid(), name), HttpStatusCode.BadRequest);
        await ShouldBeProblemAsync(await RegisterAsync(App.Auth0.AccessToken(owner.Identity.Subject), Guid.NewGuid(), new string('x', 101)), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_a_bearer_token_registers_a_phone_a_browser_session_is_refused()
    {
        var (owner, _) = await SetUpOwnerAsync();

        var response = await owner.SendAsync(HttpMethod.Post, AppSignIns, new { deviceName = "Not a phone" }, tweak: r => r.Headers.Add("X-Install-Id", Guid.NewGuid().ToString()));

        await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Someone_elses_phones_are_never_listed_even_with_the_same_install_id()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (member, memberAccount) = await AddMemberAsync(owner, account.LibraryId!.Value, "viewer");
        var install = Guid.NewGuid();

        (await RegisterAsync(App.Auth0.AccessToken(owner.Identity.Subject), install)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await RegisterAsync(App.Auth0.AccessToken(member.Identity.Subject), install)).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await CountAppSignInsAsync(account.UserId)).ShouldBe(1);
        (await CountAppSignInsAsync(memberAccount.UserId)).ShouldBe(1);
        (await ReadAsync(await member.GetAsync(AppSignIns))).GetProperty("items").GetArrayLength().ShouldBe(1);
    }
}
