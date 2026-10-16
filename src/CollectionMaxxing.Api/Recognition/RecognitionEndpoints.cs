using CollectionMaxxing.Application.Recognition;
using Microsoft.Net.Http.Headers;

namespace CollectionMaxxing.Api.Recognition;

/// <summary>
/// What the phone needs to recognise things, fetched from the PC so tuning needs no iOS build (docs/02, docs/13 "Wave 3").
/// Any signed-in member may read it; it is the same for everyone and is not tied to a library.
/// </summary>
public static class RecognitionEndpoints
{
    /// <summary>Maps this feature's routes. Called from <c>Program.cs</c>.</summary>
    public static IEndpointRouteBuilder MapRecognition(this IEndpointRouteBuilder app)
    {
        var recognition = app.MapGroup("/api/v1/recognition");

        // The ETag is the config's version, so a phone that already has it gets a 304 and no body.
        recognition.MapGet("/config", Config)
            .WithName("GetRecognitionConfig").WithTags("Recognition")
            .Produces<RecognitionConfig>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        return app;
    }

    private static IResult Config(HttpContext http, GetRecognitionConfig.Handler handler)
    {
        var config = handler.Handle();
        var etag = new EntityTagHeaderValue($"\"{config.Version}\"");

        // Cheap to answer and cheap to check, so the phone may ask every time; nothing shared may keep a copy.
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.ETag = etag.ToString();

        var known = http.Request.GetTypedHeaders().IfNoneMatch;
        if (known.Any(candidate => candidate == EntityTagHeaderValue.Any || candidate.Compare(etag, useStrongComparison: false)))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.Ok(config);
    }
}
