using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.AppDownload;

// ADR-024: the iPhone app comes from TestFlight, so the server no longer hands out an .ipa (OPS-4 is retired).
public class IpaDownloadRemovedTests(ApiFixture api) : ApiTest(api)
{
    [Theory]
    [InlineData("GET", "/api/v1/app/ipa")]
    [InlineData("HEAD", "/api/v1/app/ipa")]
    [InlineData("GET", "/api/v1/app/ipa/info")]
    public async Task The_ipa_routes_are_404_for_a_signed_in_caller(string method, string path)
    {
        using var owner = Api.CreateClient(Guid.CreateVersion7());

        var response = await owner.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_ipa_routes_and_their_dto_are_absent_from_the_live_document()
    {
        using var client = Api.CreateClient();

        var live = await client.GetStringAsync("/openapi/v1.json");

        live.ShouldNotContain("/api/v1/app/ipa");
        live.ShouldNotContain("IpaStatus");
    }

    [Fact]
    public async Task The_ipa_routes_and_their_dto_are_absent_from_the_committed_contract()
    {
        var committed = await File.ReadAllTextAsync(RepositoryFiles.Find("contracts", "openapi.json"));

        committed.ShouldNotContain("/api/v1/app/ipa");
        committed.ShouldNotContain("IpaStatus");
    }
}
