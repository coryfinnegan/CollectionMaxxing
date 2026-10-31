using System.Net;
using System.Text.Json.Nodes;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CollectionMaxxing.Api.Tests.Health;

/// <summary>
/// <c>GET /health/version</c> (#491): the commit the running server was built from, so a merge's delivery
/// can be verified against the running server instead of the image registry. Anonymous, like the other
/// <c>/health/*</c> probes, and likewise outside the OpenAPI document.
/// </summary>
public sealed class VersionEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Url = "/health/version";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Anonymous_callers_get_the_build_sha()
    {
        using var client = WithBuildSha(Sha).CreateClient();

        var response = await client.GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["sha"]!.GetValue<string>().ShouldBe(Sha);
        body["shortSha"]!.GetValue<string>().ShouldBe(Sha[..12]);
    }

    [Fact]
    public async Task Without_a_build_sha_it_reports_unknown()
    {
        using var client = api.CreateClient();

        var response = await client.GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["sha"]!.GetValue<string>().ShouldBe("unknown");
        body["shortSha"]!.GetValue<string>().ShouldBe("unknown");
    }

    [Fact]
    public async Task A_value_that_is_not_a_commit_reports_unknown()
    {
        using var client = WithBuildSha("not-a-sha").CreateClient();

        var response = await client.GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["sha"]!.GetValue<string>().ShouldBe("unknown");
    }

    private WebApplicationFactory<Program> WithBuildSha(string sha) =>
        api.Factory.WithWebHostBuilder(builder => builder.UseSetting("CM_BUILD_SHA", sha));
}
