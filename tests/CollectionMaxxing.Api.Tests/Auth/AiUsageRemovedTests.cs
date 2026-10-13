using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance test of ID-1 (issue #14): the AI-usage stub is gone (ADR-017). Do not edit or weaken: add new tests instead.
public class AiUsageRemovedTests(ApiFixture api) : ApiTest(api)
{
    [Fact]
    public async Task Get_ai_usage_is_404_even_for_the_admin()
    {
        using var admin = Api.CreateClient(Guid.CreateVersion7(), roles: "admin");
        using var anonymous = Api.CreateClient();

        (await admin.GetAsync("/api/admin/ai-usage")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync("/api/admin/ai-usage")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_ai_usage_endpoint_and_its_dto_are_absent_from_the_live_document()
    {
        using var client = Api.CreateClient();

        var live = await client.GetStringAsync("/openapi/v1.json");

        live.ShouldNotContain("ai-usage");
        live.ShouldNotContain("AiUsage");
    }

    [Fact]
    public async Task The_ai_usage_endpoint_and_its_dto_are_absent_from_the_committed_contract()
    {
        var committed = await File.ReadAllTextAsync(RepositoryFiles.Find("contracts", "openapi.json"));

        committed.ShouldNotContain("ai-usage");
        committed.ShouldNotContain("AiUsage");
    }
}
