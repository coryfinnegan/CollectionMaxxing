using System.Net;
using CollectionMaxxing.Api.Auth;
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
/// <c>RequireLibraryRole</c> must enforce membership by itself, wherever the endpoint is mapped. These tests map probe
/// endpoints OUTSIDE the library route group (so no group filter helps) and check they are still guarded.
/// </summary>
public class RequireLibraryRoleEnforcementTests(ApiFixture api) : ApiTest(api)
{
    private const string ViewerRoute = "/test-only/libraries/{libraryId:guid}/viewer";
    private const string EditorRoute = "/test-only/libraries/{libraryId:guid}/editor";

    private static string ViewerUrl(Guid libraryId) => $"/test-only/libraries/{libraryId}/viewer";

    private static string EditorUrl(Guid libraryId) => $"/test-only/libraries/{libraryId}/editor";

    /// <summary>The real app plus extra endpoints that sit outside the library group. Not shared with other tests.</summary>
    private WebApplicationFactory<Program> CreateFactoryWithProbes() => Api.Factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new ProbeStartupFilter())));

    private static HttpClient ClientAs(WebApplicationFactory<Program> factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        return client;
    }

    [Fact]
    public async Task A_non_member_gets_404_from_an_endpoint_outside_the_library_group()
    {
        var library = await CreateLibraryAsync();
        var otherLibrary = await CreateLibraryAsync();
        await using var factory = CreateFactoryWithProbes();
        using var client = ClientAs(factory, otherLibrary.OwnerId);

        using var response = await client.GetAsync(ViewerUrl(library.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_role_below_the_minimum_gets_403_from_an_endpoint_outside_the_library_group()
    {
        var library = await CreateLibraryAsync();
        await using var factory = CreateFactoryWithProbes();
        using var client = ClientAs(factory, library.ViewerId);

        using var response = await client.GetAsync(EditorUrl(library.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_member_with_a_high_enough_role_reaches_an_endpoint_outside_the_library_group()
    {
        var library = await CreateLibraryAsync();
        await using var factory = CreateFactoryWithProbes();
        using var client = ClientAs(factory, library.ViewerId);

        using var response = await client.GetAsync(ViewerUrl(library.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed class ProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet(ViewerRoute, () => Results.Ok()).RequireLibraryRole(LibraryRole.Viewer);
                endpoints.MapGet(EditorRoute, () => Results.Ok()).RequireLibraryRole(LibraryRole.Editor);
            });
        };
    }
}
