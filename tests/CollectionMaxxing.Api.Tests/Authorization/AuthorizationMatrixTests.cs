using System.Text;
using System.Text.RegularExpressions;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Authorization;

public partial class AuthorizationMatrixTests(ApiFixture api) : ApiTest(api)
{
    [Theory]
    [MemberData(nameof(AuthorizationMatrix.Rows), MemberType = typeof(AuthorizationMatrix))]
    public async Task Each_endpoint_answers_each_kind_of_caller_as_the_matrix_says(string endpointName, Actor actor)
    {
        var endpoint = AuthorizationMatrix.Cases.Single(c => c.Name == endpointName);
        var world = await ArrangeWorldAsync();
        using var client = ClientFor(actor, world);
        var url = world.Fill(endpoint.Route) + (endpoint.Query is null ? string.Empty : "?" + endpoint.Query);
        using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), url);
        if (endpoint.Body is not null)
        {
            request.Content = new StringContent(endpoint.Body, Encoding.UTF8, "application/json");
        }

        foreach (var (name, value) in endpoint.Headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        using var response = await client.SendAsync(request);

        ((int)response.StatusCode).ShouldBe(endpoint.ExpectedFor(actor), $"{endpoint.Name} as {actor}");
    }

    /// <summary>
    /// The data every request in the matrix runs against: a library with an owner, an editor and a viewer;
    /// a second library whose owner plays "some other user"; and in the first library one location and one scan session.
    /// A new endpoint that needs another route value (say <c>{tagId}</c>) adds a seeded row here and one line to
    /// <see cref="World.Fill"/>.
    /// </summary>
    private async Task<World> ArrangeWorldAsync()
    {
        var library = await CreateLibraryAsync();
        var otherLibrary = await CreateLibraryAsync();
        var locationId = await TestData.CreateLocationAsync(Db, library.Id, "Seeded shelf");
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        var itemId = await TestData.CreateItemAsync(Db, library);
        var tagId = await TestData.CreateTagAsync(Db, library.Id, "Seeded tag");
        return new World(library, otherLibrary.OwnerId, locationId, sessionId, itemId, tagId);
    }

    private HttpClient ClientFor(Actor actor, World world) => actor switch
    {
        Actor.Anonymous => Api.CreateClient(),
        Actor.OtherUser => Api.CreateClient(world.OtherUserId),
        Actor.Viewer => Api.CreateClient(world.Library.ViewerId),
        Actor.Editor => Api.CreateClient(world.Library.EditorId),
        Actor.Owner => Api.CreateClient(world.Library.OwnerId),
        Actor.Admin => Api.CreateClient(Guid.CreateVersion7(), roles: "admin"),
        _ => throw new ArgumentOutOfRangeException(nameof(actor)),
    };

    private sealed record World(TestLibrary Library, Guid OtherUserId, Guid LocationId, Guid SessionId, Guid ItemId, Guid TagId)
    {
        /// <summary>Replaces <c>{libraryId:guid}</c>-style route parameters with real ids.</summary>
        public string Fill(string route) => RouteParameter().Replace(route, match => match.Groups["name"].Value switch
        {
            "libraryId" => Library.Id.ToString(),

            // A scan session's id is looked up (the stored session decides the library); any other id is never read by a stub.
            "id" when route.StartsWith("/api/v1/scan-sessions/", StringComparison.Ordinal) => SessionId.ToString(),
            "id" when route.Contains("/items/", StringComparison.Ordinal) => ItemId.ToString(),
            "id" when route.Contains("/tags/", StringComparison.Ordinal) => TagId.ToString(),
            "id" => LocationId.ToString(),
            "cid" or "userId" or "assetId" => Guid.CreateVersion7().ToString(),
            "token" => "a-token",
            var other => throw new InvalidOperationException($"The matrix has no seeded value for route parameter '{other}'. Add one in ArrangeWorldAsync."),
        });
    }

    [GeneratedRegex(@"\{(?<name>\w+)(:[^}]+)?\}")]
    private static partial Regex RouteParameter();
}
