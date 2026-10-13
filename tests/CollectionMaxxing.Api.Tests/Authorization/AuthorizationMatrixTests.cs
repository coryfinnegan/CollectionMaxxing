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
        using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), world.Fill(endpoint.Route));
        if (endpoint.Body is not null)
        {
            request.Content = new StringContent(endpoint.Body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);

        ((int)response.StatusCode).ShouldBe(endpoint.ExpectedFor(actor), $"{endpoint.Name} as {actor}");
    }

    /// <summary>
    /// The data every request in the matrix runs against: a library with an owner, an editor and a viewer;
    /// a second library whose owner plays "some other user"; and one location in the first library.
    /// A new endpoint that needs another route value (say <c>{itemId}</c>) adds a seeded row here and one line to
    /// <see cref="World.Fill"/>.
    /// </summary>
    private async Task<World> ArrangeWorldAsync()
    {
        var library = await CreateLibraryAsync();
        var otherLibrary = await CreateLibraryAsync();
        var locationId = await TestData.CreateLocationAsync(Db, library.Id, "Seeded shelf");
        return new World(library, otherLibrary.OwnerId, locationId);
    }

    private HttpClient ClientFor(Actor actor, World world) => actor switch
    {
        Actor.Anonymous => Api.CreateClient(),
        Actor.OtherUser => Api.CreateClient(world.OtherUserId),
        Actor.Viewer => Api.CreateClient(world.Library.ViewerId),
        Actor.Editor => Api.CreateClient(world.Library.EditorId),
        Actor.Owner => Api.CreateClient(world.Library.OwnerId),
        _ => throw new ArgumentOutOfRangeException(nameof(actor)),
    };

    private sealed record World(TestLibrary Library, Guid OtherUserId, Guid LocationId)
    {
        /// <summary>Replaces <c>{libraryId:guid}</c>-style route parameters with real ids.</summary>
        public string Fill(string route) => RouteParameter().Replace(route, match => match.Groups["name"].Value switch
        {
            "libraryId" => Library.Id.ToString(),
            "id" => LocationId.ToString(),
            var other => throw new InvalidOperationException($"The matrix has no seeded value for route parameter '{other}'. Add one in ArrangeWorldAsync."),
        });
    }

    [GeneratedRegex(@"\{(?<name>\w+)(:[^}]+)?\}")]
    private static partial Regex RouteParameter();
}
