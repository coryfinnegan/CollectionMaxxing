namespace CollectionMaxxing.Api.Tests.Authorization;

/// <summary>The kinds of caller the matrix tries against every endpoint.</summary>
public enum Actor
{
    /// <summary>Not signed in.</summary>
    Anonymous,

    /// <summary>Signed in, but a member of some other library only.</summary>
    OtherUser,

    /// <summary>A viewer of the library under test.</summary>
    Viewer,

    /// <summary>An editor of the library under test.</summary>
    Editor,

    /// <summary>The owner of the library under test.</summary>
    Owner,
}

/// <summary>
/// One endpoint and the HTTP status each kind of caller must get. <c>Route</c> is the route template exactly as
/// the endpoint declares it; <c>{libraryId}</c> and <c>{id}</c> are filled in with a seeded library and location
/// (see <see cref="AuthorizationMatrixTests"/> for how to seed more).
/// </summary>
public sealed record EndpointCase(
    string Method,
    string Route,
    string? Body,
    int Anonymous,
    int OtherUser,
    int Viewer,
    int Editor,
    int Owner)
{
    public string Name => $"{Method} {Route}";

    public int ExpectedFor(Actor actor) => actor switch
    {
        Actor.Anonymous => Anonymous,
        Actor.OtherUser => OtherUser,
        Actor.Viewer => Viewer,
        Actor.Editor => Editor,
        Actor.Owner => Owner,
        _ => throw new ArgumentOutOfRangeException(nameof(actor)),
    };
}

/// <summary>
/// THE authorization matrix (docs/07, test 2). Every endpoint of the API has a row here; a test fails when an
/// endpoint is missing. To cover a new endpoint, add one row: no other test code changes.
/// </summary>
public static class AuthorizationMatrix
{
    /// <summary>Endpoints that may be called without signing in. Anything else must require a signed-in caller.</summary>
    public static readonly IReadOnlyList<string> AnonymousAllowList =
    [
        "/health/live",
        "/health/ready",
        "/openapi/{documentName}.json",
    ];

    private const string Libraries = "/api/v1/libraries/{libraryId:guid}";

    public static readonly IReadOnlyList<EndpointCase> Cases =
    [
        //            method    route                                  body                       anon other viewer editor owner
        new EndpointCase("GET",    "/api/v1/me",                       null,                        401, 200, 200, 200, 200),

        new EndpointCase("GET",    $"{Libraries}/locations",           null,                        401, 404, 200, 200, 200),
        new EndpointCase("POST",   $"{Libraries}/locations",           """{ "name": "New shelf" }""", 401, 404, 403, 201, 201),
        new EndpointCase("PATCH",  $"{Libraries}/locations/{{id:guid}}", """{ "name": "Renamed" }""", 401, 404, 403, 200, 200),
        new EndpointCase("DELETE", $"{Libraries}/locations/{{id:guid}}", null,                      401, 404, 403, 204, 204),
    ];

    /// <summary>One theory row per endpoint and caller, so a failure names both.</summary>
    public static TheoryData<string, Actor> Rows
    {
        get
        {
            var rows = new TheoryData<string, Actor>();
            foreach (var endpoint in Cases)
            {
                foreach (var actor in Enum.GetValues<Actor>())
                {
                    rows.Add(endpoint.Name, actor);
                }
            }

            return rows;
        }
    }
}
