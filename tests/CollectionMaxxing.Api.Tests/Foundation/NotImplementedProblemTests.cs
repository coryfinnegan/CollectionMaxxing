using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Foundation;

/// <summary>
/// Every endpoint of the v1 contract exists before it works. Until a stream fills one in, it answers 501 as Problem Details,
/// through one shared helper, so a client (and the contract) can already see the whole API.
/// </summary>
public class NotImplementedProblemTests(ApiFixture api) : ApiTest(api)
{
    [Fact]
    public async Task A_stubbed_endpoint_answers_501_as_problem_details()
    {
        var library = await CreateLibraryAsync();

        using var response = await ClientFor(library, LibraryRole.Viewer).GetAsync($"/api/v1/libraries/{library.Id}/items");

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("status").GetInt32().ShouldBe(501);
        problem.GetProperty("title").GetString().ShouldBe("Not implemented");
        problem.GetProperty("type").GetString().ShouldBe(ProblemResults.TypeFor("not_implemented"));
        problem.GetProperty("code").GetString().ShouldBe("not_implemented");
        problem.TryGetProperty("traceId", out _).ShouldBeTrue();
        problem.GetProperty("detail").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task The_helper_builds_a_501_problem()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await ProblemResults.NotImplemented().ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status501NotImplemented);
    }

    [Fact]
    public void Every_stub_is_marked_so_the_remaining_work_can_be_listed()
    {
        var stubs = Api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<StubEndpointMetadata>() is not null)
            .ToList();

        stubs.Count.ShouldBeGreaterThan(50);
    }
}
