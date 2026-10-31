using CollectionMaxxing.Api.Http;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Application.Scanning.Provisional;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>What <c>POST …/candidates/{cid}/crop</c> takes: one multipart part named <c>file</c>. (Only describes the body for the contract; the endpoint reads the stream itself.)</summary>
public sealed class CandidateCropForm
{
    /// <summary>A JPEG crop of the row's segment, at most 1 MB and 4096 pixels a side.</summary>
    public IFormFile? File { get; init; }
}

/// <summary>
/// <c>POST /api/v1/scan-sessions/{id}/candidates/{cid}/crop</c> (ADR-030). The body is read straight from the request into memory,
/// at most 1 MB of it, so the uploaded original is never spooled to a temporary file on disk: only the cleaned copy is ever stored.
/// The route is not bound to a form model, so the framework's antiforgery check (for cookie forms) does not apply; what protects the write is
/// what protects every other write: the Bearer [REDACTED], or the session cookie together with the <c>X-CSRF</c> header (<c>CsrfMiddleware</c>).
/// </summary>
internal static class CropEndpoint
{
    /// <summary>The name of the multipart part that holds the picture.</summary>
    public const string PartName = "file";

    /// <summary>Room for the multipart framing around a 1 MB file.</summary>
    private const int Overhead = 64 * 1024;

    private const int MaxRequestBytes = JpegSanitizer.CropMaxBytes + Overhead;

    public static async Task<IResult> UploadAsync(
        Guid id, Guid cid, HttpRequest request, UploadCandidateCrop.Handler handler, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemResults.Problem(
                StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", "Send the crop as multipart/form-data with a part named \"file\".", "scan.crop_not_multipart");
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200)
        {
            return Bad("scan.crop_no_file", "The upload has no multipart boundary.");
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
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.IsFileDisposition()
                    || HeaderUtilities.RemoveQuotes(disposition.Name).Value != PartName)
                {
                    continue; // not the picture: ignored, and drained by the next read
                }

                if (file is not null)
                {
                    return Bad("scan.crop_one_file", "Send exactly one file.");
                }

                // A part has no declared length, so it is read in chunks. Null is a picture over the cap.
                file = await BoundedBody.ReadAsync(section.Body, JpegSanitizer.CropMaxBytes, declaredLength: null, cancellationToken);
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
            return Bad("scan.crop_malformed", "That upload couldn't be read as multipart/form-data.");
        }

        if (file is null)
        {
            return Bad("scan.crop_no_file", "Send the crop as a multipart part named \"file\".");
        }

        var result = await handler.HandleAsync(new UploadCandidateCrop.Command(id, cid, file), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static IResult Bad(string code, string detail) => ProblemResults.Problem(StatusCodes.Status400BadRequest, "Bad request", detail, code);

    private static IResult TooLarge() => ProblemResults.Problem(
        StatusCodes.Status413PayloadTooLarge, "Payload too large", "That crop is over 1 MB. Send a smaller crop.", "scan.crop_too_large");
}
