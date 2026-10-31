using System.Text.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Api.Scanning;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Evals;

/// <summary>
/// The eval server's routes, all under <c>/api/admin</c> and so behind <see cref="Policies.Admin"/>: the owner's golden
/// photos go out (and new ones come in from the phone, at full size), the phone's per-photo readings come back, and the report is built on the PC. The routes are always mapped (so
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

        // One golden photo from the phone, at full size, into the PC's golden folder. The body is the picture's bytes; what it is, is decided
        // from them. The only route with a body this big (40 MB), and it has the long upload timeout.
        evals.MapPost("/golden/{set}/{id}", AddGoldenPhotoAsync)
            .WithName("AddGoldenPhoto")
            .Accepts<Stream>("*/*") // any header: the bytes decide what the picture is (and a header that names a type must not make the router refuse it)
            .Produces<GoldenPhotoDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .WithRequestTimeout(Security.SecurityHeaders.UploadTimeoutPolicy); // 40 MB over a slow link takes longer than the usual minute

        // What the phone read in each golden photo. Grounded and scored on the PC; the report is written there.
        evals.MapPost("/runs", SubmitRunAsync)
            .WithName("SubmitEvalRun")
            .Accepts<EvalRunRequest>("application/json")
            .Produces<EvalRunResult>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .WithRequestTimeout(Security.SecurityHeaders.UploadTimeoutPolicy); // a big report over a slow link

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
        strict.Converters.Insert(0, new LenientItemBoxConverter()); // a box is a hint: one that cannot be read is no box, not a refused run
        strict.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; // a member the run does not declare is refused, not dropped
        return strict;
    }

    private static async Task<IResult> SubmitRunAsync(
        HttpContext http,
        IGoldenSet golden,
        IOptions<JsonOptions> json,
        SubmitEvalRun.Handler handler,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Every refusal is logged with its problem code (never the body), so a 400 can be explained from the server log.
        var logger = loggerFactory.CreateLogger("CollectionMaxxing.Api.Evals.SubmitEvalRun");

        // Off means 404 before anything about the body is looked at.
        if (!golden.IsAvailable)
        {
            return Refused(logger, EvalErrors.NotFound);
        }

        var request = http.Request;
        if (!IsJson(request.ContentType))
        {
            return Refused(
                logger,
                "evals.json_required",
                ProblemResults.Problem(
                    StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", "Send the run as application/json.", "evals.json_required"));
        }

        if (request.ContentLength > EvalsOptions.MaxRunBytes)
        {
            return Refused(logger, "evals.body_too_large", TooLarge());
        }

        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = EvalsOptions.MaxRunBytes;
        }

        byte[]? body;
        try
        {
            body = await ReadBoundedAsync(request.Body, EvalsOptions.MaxRunBytes, request.ContentLength, cancellationToken);
        }
        catch (BadHttpRequestException)
        {
            return Refused(logger, "evals.body_too_large", TooLarge()); // the server's own cap tripped on a body sent without a length
        }

        if (body is null)
        {
            return Refused(logger, "evals.body_too_large", TooLarge());
        }

        EvalRunRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EvalRunRequest>(body, StrictOptions(json.Value.SerializerOptions));
        }
        catch (JsonException exception)
        {
            // The path inside the body names the member that could not be read (never its value).
            return Refused(logger, "evals.body_invalid", BadBody(), exception.Path);
        }

        if (parsed is null)
        {
            return Refused(logger, "evals.body_invalid", BadBody());
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
                            new IdentifyItemInput(i.LocalId, i.SegmentId, i.Text, i.Title, i.Creator, i.Edition, i.Completeness, i.Kind, i.Format, i.Year, i.Platform, null, i.ProductCode,
                                i.Box is { } box ? new ReadBoxInput(box.X, box.Y, box.Width, box.Height) : null, i.Unconfirmed == true),
                            i.ResolvedBy,
                            i.Tier,
                            i.Provisional == true,
                            i.IsGuess)).ToList(),
                    r.UnreadableCount,
                    r.LatencyMs,
                    r.FirstItemMs,
                    r.ThermalState,
                    r.CapturedCount,
                    r.CapturedAt,
                    r.DuplicateRescan is { } evidence ? new DuplicateRescanInput(evidence.OwnedHits, evidence.AnsweredOnCard, evidence.UnsolicitedAdds) : null,
                    r.OcrLines?.Select(l => l is null ? null! : new EvalOcrLineInput(l.Text, l.X, l.Y, l.Width, l.Height)).ToList(),
                    r.OcrPieces,
                    r.CoverCue)).ToList(),
            parsed.Partial == true,
            parsed.BaselineNote);

        // Grounding a big run takes minutes, and the phone may lock or drop the connection meanwhile; tying the work to the
        // request token would cancel it and throw the whole run away, unsaved. The body is read; from here the work belongs
        // to the PC, not to the connection. It stops when the app shuts down, not when the phone goes away (and the upload
        // timeout, which cancels the same token, does not cut it short either).
        var result = await handler.HandleAsync(input, lifetime.ApplicationStopping);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : Refused(logger, result.Error!);
    }

    private static IResult Refused(ILogger logger, CollectionMaxxing.Domain.Common.DomainError error)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Eval run refused: {ProblemCode} at {Field}.", error.Code, error.Field);
        }

        return error.ToProblem();
    }

    private static IResult Refused(ILogger logger, string code, IResult problem, string? field = null)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Eval run refused: {ProblemCode} at {Field}.", code, field);
        }

        return problem;
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

    /// <summary>
    /// The body, or null when it is longer than <paramref name="maxBytes"/>. Reads at most one byte past the limit. A length the caller declared
    /// (and that fits) sizes the buffer once, so a 30 MB original is held once and not copied again.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int maxBytes, long? declaredLength, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(declaredLength is > 0 and <= int.MaxValue ? (int)Math.Min(declaredLength.Value, maxBytes) : 0);
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.TryGetBuffer(out var segment) && segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length
            ? array
            : buffer.ToArray();
    }

    // ---- adding a golden photo ---------------------------------------------------------------------------------------------

    private static async Task<IResult> AddGoldenPhotoAsync(
        string set,
        string id,
        HttpContext http,
        AddGoldenPhoto.Handler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Every refusal is logged with its code and nothing the caller sent: not the bytes, and not the name (it may not be a name at all).
        var logger = loggerFactory.CreateLogger("CollectionMaxxing.Api.Evals.AddGoldenPhoto");

        // Off is a 404, and a set or name that cannot be used a 400, before any of the body is read.
        var check = handler.Check(set, id);
        if (!check.IsSuccess)
        {
            return PhotoRefused(logger, check.Error!);
        }

        var request = http.Request;
        if (request.ContentLength > EvalsOptions.MaxGoldenPhotoBytes)
        {
            return PhotoRefused(logger, "evals.photo_too_large", PhotoTooLarge());
        }

        // This route alone takes a body this big: the cap is raised for this request only, and Kestrel enforces it for a body sent without a
        // length. (Not every server lets it be changed: then the read cap below is the guard.)
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = EvalsOptions.MaxGoldenPhotoBytes;
        }

        byte[]? bytes;
        try
        {
            bytes = await ReadBoundedAsync(request.Body, EvalsOptions.MaxGoldenPhotoBytes, request.ContentLength, cancellationToken);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return PhotoRefused(logger, "evals.photo_too_large", PhotoTooLarge());
        }

        if (bytes is null)
        {
            return PhotoRefused(logger, "evals.photo_too_large", PhotoTooLarge());
        }

        // The bytes say what the picture is. The Content-Type the phone sent is never read.
        if (GoldenPhotoFormats.Sniff(bytes) is not { } format)
        {
            return PhotoRefused(
                logger,
                "evals.unsupported_picture",
                ProblemResults.Problem(
                    StatusCodes.Status415UnsupportedMediaType,
                    "Unsupported media type",
                    "Send the photo as a JPEG or a HEIC, the original from your library.",
                    "evals.unsupported_picture"));
        }

        var result = handler.Handle(set, id, format, bytes);
        if (!result.IsSuccess)
        {
            return PhotoRefused(logger, result.Error!);
        }

        // The name was checked to be plain letters, digits, "-" and "_" before it got here, so it is safe to write in a log line.
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Golden photo added: {Set}/{Id}, {Bytes} bytes.", set, id, bytes.Length);
        }

        return TypedResults.Created($"/api/admin/evals/golden/{id}/image", result.Value);
    }

    private static IResult PhotoRefused(ILogger logger, CollectionMaxxing.Domain.Common.DomainError error) =>
        PhotoRefused(logger, error.Code, error.ToProblem());

    private static IResult PhotoRefused(ILogger logger, string code, IResult problem)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Golden photo refused: {ProblemCode}.", code);
        }

        return problem;
    }

    private static IResult PhotoTooLarge() => ProblemResults.Problem(
        StatusCodes.Status413PayloadTooLarge,
        "Payload too large",
        $"A golden photo can be at most {EvalsOptions.MaxGoldenPhotoBytes / (1024 * 1024)} MB.",
        "evals.photo_too_large");
}
