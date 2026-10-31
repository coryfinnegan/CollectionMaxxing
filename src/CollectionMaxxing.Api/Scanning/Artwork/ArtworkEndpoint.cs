using System.Text.Json;
using CollectionMaxxing.Api.Http;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Scanning.Artwork;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Scanning.Artwork;

/// <summary>
/// <c>POST /api/v1/scan-sessions/{id}/candidates/{cid}:art</c>: the feature-print distances the phone measured between an item and
/// its close-call alternatives' artwork (docs/04). The body is read here, after the session and role filters, and bounded: a
/// stranger's session is 404 whatever the body looks like, and a body of more than 4 KB is refused with a problem. A release
/// that is not one of the candidate's own alternatives is 422.
/// </summary>
internal static class ArtworkEndpoint
{
    /// <summary>Five distances are well under 1 KB; anything near this is not a list of distances.</summary>
    public const int MaxBodyBytes = 4 * 1024;

    public static async Task<IResult> HandleAsync(
        Guid id,
        Guid cid,
        HttpContext http,
        IOptions<JsonOptions> json,
        ScoreCandidateArtwork.Handler handler,
        CancellationToken cancellationToken)
    {
        var request = http.Request;
        if (!IsJson(request.ContentType))
        {
            return ProblemResults.Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported media type",
                "Send the distances as application/json.",
                "scan.json_required");
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            return TooLarge();
        }

        var body = await BoundedBody.ReadAsync(request.Body, MaxBodyBytes, request.ContentLength, cancellationToken);
        if (body is null)
        {
            return TooLarge();
        }

        ArtDistancesRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ArtDistancesRequest>(body, json.Value.SerializerOptions);
        }
        catch (JsonException)
        {
            return BadBody();
        }

        if (parsed is null)
        {
            return BadBody();
        }

        var distances = parsed.Distances?.Select(d => d is null ? null : new ReportedDistance(d.ReleaseId, d.Distance)).ToList();
        var result = await handler.HandleAsync(new ScoreCandidateArtwork.Command(id, cid, distances), cancellationToken);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        // A release the candidate doesn't offer is well-formed but wrong: 422, with nothing about the release in the answer.
        return result.Error!.Code == ArtworkErrors.ReleaseNotAnOptionCode
            ? ProblemResults.Problem(StatusCodes.Status422UnprocessableEntity, "Unprocessable", result.Error.Message, result.Error.Code)
            : result.Error.ToProblem();
    }

    private static bool IsJson(string? contentType) =>
        contentType is not null
        && (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    private static IResult BadBody() => ProblemResults.Problem(
        StatusCodes.Status400BadRequest, "Bad request", "The body isn't valid JSON for artwork distances.", "scan.body_invalid");

    private static IResult TooLarge() => ProblemResults.Problem(
        StatusCodes.Status413PayloadTooLarge,
        "Payload too large",
        $"Artwork distances can be at most {MaxBodyBytes / 1024} KB.",
        "scan.body_too_large");
}
