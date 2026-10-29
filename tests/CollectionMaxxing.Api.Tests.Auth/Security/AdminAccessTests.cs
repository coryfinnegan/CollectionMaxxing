using System.Net;
using System.Net.Http.Headers;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// <c>/api/admin/**</c> belongs to the platform administrator, who is the person who set the server up (docs/07, layer 4). Library roles are
/// not platform roles: an invited editor is not an admin. A phone carries the role when its account holds it (#523), so the platform admin's
/// phone gets in and anyone else's phone doesn't. Every admin route in the routing table is tried, with real sign-in.
/// </summary>
public class AdminAccessTests(ApiFixture api) : InviteTest(api)
{
    private List<(string Method, string Url)> AdminRoutes() =>
    [
        .. EndpointFacts.Read(Api.Factory.Services.GetRequiredService<EndpointDataSource>())
            .Where(e => e.Route.StartsWith(EndpointAudit.AdminPrefix, StringComparison.Ordinal))
            .Select(e => (e.Method, Url: e.Route.Replace("{id:guid}", Guid.NewGuid().ToString(), StringComparison.Ordinal).Replace("{id}", "x", StringComparison.Ordinal))),
    ];

    [Fact]
    public async Task The_sweep_finds_the_admin_routes()
    {
        await Task.CompletedTask;

        AdminRoutes().Count.ShouldBeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public async Task Only_the_platform_admin_gets_into_any_admin_route_over_either_way_in_and_everyone_else_gets_403()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;

        // ONB-1 (ADR-027): nobody becomes the platform admin by using the app any more; the role is given on the host. Give it to the owner
        // here, and sign in again so the session carries it.
        using (var scope = App.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.AppUser>>();
            (await roles.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole<Guid>("admin"))).Succeeded.ShouldBeTrue();
            (await users.AddToRoleAsync((await users.FindByIdAsync(account.UserId.ToString()))!, "admin")).Succeeded.ShouldBeTrue();
        }

        owner.ClearCookies();
        (await owner.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        var (editor, _) = await AddMemberAsync(owner, libraryId, "editor", "Edith");
        var (viewer, _) = await AddMemberAsync(owner, libraryId, "viewer", "Vic");
        var ownerPhone = await SignInPhoneAsync(owner, "Owner phone");
        var editorPhone = await SignInPhoneAsync(editor, "Editor phone", App.NewBrowser().Http);
        var anonymous = App.NewBrowser();

        var failures = new List<string>();
        foreach (var (method, url) in AdminRoutes())
        {
            async Task<int> StatusAsync(Func<Task<HttpResponseMessage>> call) => (int)(await call()).StatusCode;
            Task<HttpResponseMessage> AsBrowser(Auth.WebClient who) => who.SendAsync(new HttpMethod(method), url, method == "GET" ? null : new { });
            Task<HttpResponseMessage> AsPhone(string token) => AsDeviceAsync(new HttpMethod(method), url, token);

            var asAdmin = await StatusAsync(() => AsBrowser(owner));
            if (asAdmin is 401 or 403)
            {
                failures.Add($"{method} {url}: the admin was refused ({asAdmin})");
            }

            var asAdminPhone = await StatusAsync(() => AsPhone(ownerPhone.AccessToken));
            if (asAdminPhone is 401 or 403)
            {
                failures.Add($"{method} {url}: the admin's phone was refused ({asAdminPhone})");
            }

            foreach (var (who, status) in new[]
            {
                ("an editor", await StatusAsync(() => AsBrowser(editor))),
                ("a viewer", await StatusAsync(() => AsBrowser(viewer))),
                ("an editor's phone", await StatusAsync(() => AsPhone(editorPhone.AccessToken))),
            })
            {
                if (status != 403)
                {
                    failures.Add($"{method} {url}: {who} got {status}, wanted 403");
                }
            }

            var anonymousStatus = await StatusAsync(() => AsBrowser(anonymous));
            if (anonymousStatus != 401)
            {
                failures.Add($"{method} {url}: anonymous got {anonymousStatus}, wanted 401");
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_a_caller_sends_makes_them_an_admin()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var (editor, _) = await AddMemberAsync(owner, account.LibraryId!.Value, "editor");

        foreach (var (name, value) in new[] { ("X-Admin", "true"), ("X-Roles", "admin"), ("X-Test-Roles", "admin"), ("X-Test-User", account.UserId.ToString()), ("X-Forwarded-User", "owner"), ("Remote-User", "owner") })
        {
            var response = await editor.SendAsync(HttpMethod.Get, "/api/admin/jobs?admin=true&role=admin&roles=admin", null, tweak: r => r.Headers.TryAddWithoutValidation(name, value));
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, name);
        }

        var asCookie = await editor.SendAsync(HttpMethod.Get, "/api/admin/jobs", null, tweak: r =>
            r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String("owner:admin"u8.ToArray())));
        asCookie.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_admin_role_is_not_a_library_role_and_gives_no_way_into_a_library_that_is_not_theirs()
    {
        var (owner, _) = await SetUpOwnerAsync();
        var other = await CreateLibraryAsync();

        var response = await owner.GetAsync($"/api/v1/libraries/{other.Id}/items");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
