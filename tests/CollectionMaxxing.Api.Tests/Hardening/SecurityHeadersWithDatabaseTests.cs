using System.Net;
using CollectionMaxxing.Api.Security;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Hardening;

/// <summary>The answers that need a working database: a signed-in 200, and a 404 for something that is not there.</summary>
public class SecurityHeadersWithDatabaseTests(ApiFixture api) : ApiTest(api)
{
    [Fact]
    public async Task A_signed_in_200_and_a_404_carry_the_headers_and_are_never_cached()
    {
        var library = await CreateLibraryAsync();
        using var viewer = ClientFor(library, LibraryRole.Viewer);

        using var ok = await viewer.GetAsync("/api/v1/libraries", TestContext.Current.CancellationToken);
        using var missing = await viewer.GetAsync($"/api/v1/libraries/{library.Id}/items/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        foreach (var response in new[] { ok, missing })
        {
            response.Headers.GetValues("Content-Security-Policy").ShouldBe([SecurityHeaders.ContentSecurityPolicy]);
            response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
            response.Headers.GetValues("Referrer-Policy").ShouldBe(["strict-origin-when-cross-origin"]);
            response.Headers.GetValues("Permissions-Policy").ShouldBe(["camera=(self), geolocation=()"]);
            response.Headers.GetValues("Cross-Origin-Opener-Policy").ShouldBe(["same-origin"]);
            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        }
    }

    [Fact]
    public void Kestrel_is_limited_to_sixteen_megabytes_a_request_and_does_not_announce_itself()
    {
        var kestrel = Api.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>().Value;

        kestrel.Limits.MaxRequestBodySize.ShouldBe(16 * 1024 * 1024);
        kestrel.AddServerHeader.ShouldBeFalse();
    }
}
