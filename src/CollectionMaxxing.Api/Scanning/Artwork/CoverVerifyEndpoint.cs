using System.Text.Json;
using CollectionMaxxing.Api.Http;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Artwork;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Common;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace CollectionMaxxing.Api.Scanning.Artwork;

/// <summary>What <c>POST …/candidates/{cid}:verify-cover</c> takes: the capture, and the detected cover quad when the phone has one. (Only describes the body for the contract; the endpoint reads the stream itself.)</summary>
public sealed class CoverVerifyForm
{
    /// <summary>A JPEG of the cover, at most 4 MB.</summary>
    public IFormFile? File { get; init; }

    /// <summary>The cover corners as JSON (<c>{"topLeft":{"x","y"},…}</c>, origin top-left, 0–1). Optional.</summary>
    public string? Quad { get; init; }
}

/// <summary>
/// <c>POST /api/v1/scan-sessions/{id}/candidates/{cid}:verify-cover</c>: the server compares the capture against the
/// alternatives' covers, re-ranks them and preselects a clear match (#438 stage 2). The capture is verified and
/// discarded, never stored. Read straight from the request like the photo upload: capped, never spooled, no form
/// binding and no antiforgery token.
/// </summary>
internal static class CoverVerifyEndpoint
{
    /// <summary>The multipart part that holds the picture.</summary>
    public const string FilePartName = "file";

    /// <summary>The multipart part that holds the quad JSON, when the phone detected the cover corners.</summary>
    public const string QuadPartName = "quad";

    /// <summary>The most bytes of quad JSON the endpoint reads: four points never need more.</summary>
    public const int MaxQuadBytes = 1024;

    /// <summary>Room for the multipart framing and the quad around a 4 MB file.</summary>
    private const int Overhead = 65 * 1024;

    private const int MaxRequestBytes = JpegSanitizer.MaxBytes + Overhead;

    private sealed record QuadPointDto(double X, double Y);

    private sealed record QuadDto(QuadPointDto? TopLeft, QuadPointDto? TopRight, QuadPointDto? BottomRight, QuadPointDto? BottomLeft);

    public static async Task<IResult> HandleAsync(
        Guid id,
        Guid cid,
        HttpRequest request,
        IOptions<JsonOptions> jsonOptions,
        VerifyCoverArtwork.Handler handler,
        CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemResults.Problem(
                StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", "Send the capture as multipart/form-data with a part named \"file\".", "scan.cover_not_multipart");
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200)
        {
            return Bad("scan.cover_no_file", "The upload has no multipart boundary.");
        }

        if (request.ContentLength > MaxRequestBytes)
        {
            return TooLarge();
        }

        // Kestrel enforces the same cap for a body sent without a length. (Not every server lets it be changed: then the read cap below is the guard.)
        var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxRequestBytes;
        }

        byte[]? file = null;
        Result<CoverQuad?>? quad = null;
        try
        {
            var reader = new MultipartReader(boundary, request.Body)
            {
                BodyLengthLimit = MaxRequestBytes,
                HeadersCountLimit = 16,
                HeadersLengthLimit = 16 * 1024,
            };
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    continue;
                }

                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (name == QuadPartName && quad is null)
                {
                    // A quad longer than the cap is as invalid as a malformed one.
                    var quadJson = await BoundedBody.ReadTextAsync(section.Body, MaxQuadBytes, cancellationToken);
                    quad = quadJson is null ? QuadInvalid() : ParseQuad(quadJson, jsonOptions.Value.SerializerOptions);
                    continue; // the phone sends the quad as a field or a file part; both read the same
                }

                if (!disposition.IsFileDisposition() || name != FilePartName)
                {
                    continue; // not the capture: ignored, and drained by the next read
                }

                if (file is not null)
                {
                    return Bad("scan.cover_one_file", "Send exactly one capture.");
                }

                // A part has no declared length, so it is read in chunks. Null is a capture over the cap.
                file = await BoundedBody.ReadAsync(section.Body, JpegSanitizer.MaxBytes, declaredLength: null, cancellationToken);
                if (file is null)
                {
                    return TooLarge();
                }
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // Cut short or not multipart at all (a client that went away never reads this answer). Comes after the 413 above:
            // BadHttpRequestException is an IOException too.
            return Bad("scan.cover_malformed", "That upload couldn't be read as multipart/form-data.");
        }

        if (file is null)
        {
            return Bad("scan.cover_no_file", "Send the capture as a multipart part named \"file\".");
        }

        quad ??= Result<CoverQuad?>.Success(null);
        if (!quad.IsSuccess)
        {
            return quad.Error!.ToProblem();
        }

        var result = await handler.HandleAsync(new VerifyCoverArtwork.Command(id, cid, file, quad.Value), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static Result<CoverQuad?> ParseQuad(string json, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Result<CoverQuad?>.Success(null);
        }

        QuadDto? quad;
        try
        {
            quad = JsonSerializer.Deserialize<QuadDto>(json, options);
        }
        catch (JsonException)
        {
            return QuadInvalid();
        }

        if (quad is null
            || quad.TopLeft is null || quad.TopRight is null || quad.BottomRight is null || quad.BottomLeft is null)
        {
            return QuadInvalid();
        }

        return new CoverQuad(
            new CoverPoint(quad.TopLeft.X, quad.TopLeft.Y),
            new CoverPoint(quad.TopRight.X, quad.TopRight.Y),
            new CoverPoint(quad.BottomRight.X, quad.BottomRight.Y),
            new CoverPoint(quad.BottomLeft.X, quad.BottomLeft.Y));
    }

    private static Result<CoverQuad?> QuadInvalid() =>
        Result<CoverQuad?>.Failure(ScanningErrors.Invalid(
            "scan.cover_quad_invalid", "The cover quad must be four points inside the picture: {\"topLeft\":{\"x\",\"y\"},…}.", "quad"));

    private static IResult Bad(string code, string detail) => ProblemResults.Problem(StatusCodes.Status400BadRequest, "Bad request", detail, code);

    private static IResult TooLarge() => ProblemResults.Problem(
        StatusCodes.Status413PayloadTooLarge, "Payload too large", "That capture is over 4 MB. Send a smaller one.", "scan.cover_too_large");
}
