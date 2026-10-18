using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Evals;

/// <summary>
/// The eval server's routes (EVAL-1a), all under <c>/api/admin</c> and so behind <see cref="Policies.Admin"/>: the owner's golden
/// photos go out, the phone's per-photo readings come back, and the report is built on the PC. The routes are always mapped (so
/// who may call them is the same whatever the settings), and answer 404 to the administrator too until <c>Evals:Enabled</c> is
/// true and <c>Evals:GoldenPath</c> is a folder.
/// </summary>
public static class EvalsEndpoints
{
    public static IEndpointRouteBuilder MapEvals(this IEndpointRouteBuilder app)
    {
        var evals = app.MapGroup("/api/admin/evals")
            .RequireAuthorization(Policies.Admin)
            .WithTags("Admin")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // The pictures in the golden folder (ids only: never a file name or a path).
        evals.MapGet("/golden", (ListGoldenPhotos.Handler handler) => Respond(handler.Handle()))
            .WithName("ListGoldenPhotos")
            .Produces<IReadOnlyList<GoldenPhotoDto>>();

        // One picture. The id is looked up in the folder's listing; a request never reaches the file system by its own path.
        evals.MapGet("/golden/{id}/image", Image)
            .WithName("GetGoldenPhotoImage")
            .Produces(StatusCodes.Status200OK, null, "image/jpeg", "image/heic");

        // What the phone read in each golden photo. Grounded and scored on the PC; the report is written there.
        evals.MapPost("/runs", SubmitRunAsync)
            .WithName("SubmitEvalRun")
            .Accepts<EvalRunRequest>("application/json")
            .Produces<EvalRunResult>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        evals.MapGet("/runs/latest", (GetLatestEvalRun.Handler handler) => Respond(handler.Handle()))
            .WithName("GetLatestEvalRun")
            .Produces<EvalSummary>();

        return app;
    }

    private static IResult Respond<T>(CollectionMaxxing.Domain.Common.Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();

    private static IResult Image(string id, HttpContext http, GetGoldenImage.Handler handler)
    {
        var result = handler.Handle(id);
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        // The owner's own photo: private to the browser, and never sniffed as anything else.
        http.Response.Headers.CacheControl = "private, max-age=3600";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(result.Value.Content, result.Value.ContentType);
    }

    /// <summary>The app's JSON options, except that an engine is read only by its API name (no numbers, no C# member names) and an undeclared member is refused.</summary>
    private static JsonSerializerOptions StrictOptions(JsonSerializerOptions app)
    {
        var strict = new JsonSerializerOptions(app);
        strict.Converters.Insert(0, new StrictScanEngineConverter());
        strict.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; // a member the run does not declare is refused, not dropped
        return strict;
    }

    private static async Task<IResult> SubmitRunAsync(
        HttpContext http,
        IGoldenSet golden,
        IOptions<JsonOptions> json,
        SubmitEvalRun.Handler handler,
        CancellationToken cancellationToken)
    {
        // Off means 404 before anything about the body is looked at.
        if (!golden.IsAvailable)
        {
            return EvalErrors.NotFound.ToProblem();
        }

        var request = http.Request;
        if (!IsJson(request.ContentType))
        {
            return ProblemResults.Problem(
                StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", "Send the run as application/json.", "evals.json_required");
        }

        if (request.ContentLength > EvalsOptions.MaxRunBytes)
        {
            return TooLarge();
        }

        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = EvalsOptions.MaxRunBytes;
        }

        byte[]? body;
        try
        {
            body = await ReadBoundedAsync(request.Body, cancellationToken);
        }
        catch (BadHttpRequestException)
        {
            return TooLarge(); // the server's own cap tripped on a body sent without a length
        }

        if (body is null)
        {
            return TooLarge();
        }

        EvalRunRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EvalRunRequest>(body, StrictOptions(json.Value.SerializerOptions));
        }
        catch (JsonException)
        {
            return BadBody();
        }

        if (parsed is null)
        {
            return BadBody();
        }

        var input = new EvalRunInput(
            parsed.Device is null ? null : new EvalDeviceInput(parsed.Device.Model, parsed.Device.OsVersion, parsed.Device.AppVersion),
            parsed.ConfigVersion,
            parsed.Results?.Select(r => r is null
                ? null!
                : new EvalPhotoInput(
                    r.PhotoId,
                    r.Engine,
                    r.Outcome,
                    r.FellBackFrom,
                    r.Items?.Select(i => i is null
                        ? null!
                        : new EvalReadItemInput(
                            new IdentifyItemInput(i.LocalId, i.Text, i.Title, i.Creator, i.Edition, i.Completeness, i.Kind, i.Format, i.Year, i.Platform, null),
                            i.ResolvedBy)).ToList(),
                    r.UnreadableCount,
                    r.LatencyMs,
                    r.FirstItemMs,
                    r.ThermalState)).ToList(),
            parsed.Partial == true);

        var result = await handler.HandleAsync(input, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static bool IsJson(string? contentType) =>
        contentType is not null
        && (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    private static IResult BadBody() => ProblemResults.Problem(
        StatusCodes.Status400BadRequest, "Bad request", "The body isn't valid JSON for an eval run.", "evals.body_invalid");

    private static IResult TooLarge() => ProblemResults.Problem(
        StatusCodes.Status413PayloadTooLarge,
        "Payload too large",
        $"An eval run can be at most {EvalsOptions.MaxRunBytes / (1024 * 1024)} MB.",
        "evals.body_too_large");

    /// <summary>The body, or null when it is longer than <see cref="EvalsOptions.MaxRunBytes"/>. Reads at most one byte past the limit.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > EvalsOptions.MaxRunBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
