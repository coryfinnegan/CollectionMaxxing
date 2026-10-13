using System.Net;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Authorization;

/// <summary>
/// Scan-session routes must fail closed and <c>RequireScanSessionRole</c> must enforce by itself. These tests map probe
/// endpoints inside the scan-session group without the call (must be unreachable) and outside the group with it (must
/// still be guarded).
/// </summary>
public class RequireScanSessionRoleEnforcementTests(ApiFixture api) : ApiTest(api)
{
    private const string ViewerRoute = "/test-only/scan-sessions/{id:guid}/viewer";
    private const string EditorRoute = "/test-only/scan-sessions/{id:guid}/editor";
    private const string UnguardedRoute = "/{id:guid}/unguarded-probe";

    private static string ViewerUrl(Guid sessionId) => $"/test-only/scan-sessions/{sessionId}/viewer";

    private static string EditorUrl(Guid sessionId) => $"/test-only/scan-sessions/{sessionId}/editor";

    private static string UnguardedUrl(Guid sessionId) => $"/api/v1/scan-sessions/{sessionId}/unguarded-probe";

    /// <summary>The real app plus extra endpoints. Not shared with other tests.</summary>
    private WebApplicationFactory<Program> CreateFactoryWithProbes() => Api.Factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new ProbeStartupFilter())));

    private static HttpClient ClientAs(WebApplicationFactory<Program> factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        return client;
    }

    [Fact]
    public async Task An_endpoint_in_the_scan_session_group_that_forgot_RequireScanSessionRole_is_never_reached()
    {
        var library = await CreateLibraryAsync();
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        await using var factory = CreateFactoryWithProbes();
        using var client = ClientAs(factory, library.OwnerId);

        // Even the owner of the session's library gets nowhere: the group fails closed.
        HttpStatusCode? status = null;
        try
        {
            using var response = await client.GetAsync(UnguardedUrl(sessionId));
            status = response.StatusCode;
        }
        catch (InvalidOperationException)
        {
            // The test server rethrows the filter's exception: the handler was not reached.
        }

        status.ShouldNotBe(HttpStatusCode.OK);
        status?.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_non_member_gets_404_from_an_endpoint_outside_the_scan_session_group()
    {
        var library = await CreateLibraryAsync();
        var otherLibrary = await CreateLibraryAsync();
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        await using var factory = CreateFactoryWithProbes();
        using var client = ClientAs(factory, otherLibrary.OwnerId);

        using var response = await client.GetAsync(ViewerUrl(sessionId));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_role_below_the_minimum_gets_403_from_an_endpoint_outside_the_scan_session_group()
    {
        var library = await CreateLibraryAsync();
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        await using var factory = CreateFactoryWithProbes();
        using var client = ClientAs(factory, library.ViewerId);

        using var response = await client.GetAsync(EditorUrl(sessionId));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_member_with_a_high_enough_role_reaches_an_endpoint_outside_the_scan_session_group()
    {
        var library = await CreateLibraryAsync();
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        await using var factory = CreateFactoryWithProbes();
        using var client = ClientAs(factory, library.ViewerId);

        using var response = await client.GetAsync(ViewerUrl(sessionId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_session_id_that_does_not_exist_gets_404()
    {
        var library = await CreateLibraryAsync();
        using var client = ClientFor(library, LibraryRole.Owner);

        using var response = await client.GetAsync($"/api/v1/scan-sessions/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_session_id_that_is_not_a_guid_gets_404_not_500()
    {
        var library = await CreateLibraryAsync();
        using var client = ClientFor(library, LibraryRole.Owner);

        using var response = await client.GetAsync("/api/v1/scan-sessions/not-a-guid");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed class ProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet(ViewerRoute, () => Results.Ok()).RequireScanSessionRole(LibraryRole.Viewer);
                endpoints.MapGet(EditorRoute, () => Results.Ok()).RequireScanSessionRole(LibraryRole.Editor);
                // In the group, but without RequireScanSessionRole: must fail closed.
                endpoints.MapScanSessionGroup().MapGet(UnguardedRoute, () => Results.Ok());
            });
        };
    }
}
