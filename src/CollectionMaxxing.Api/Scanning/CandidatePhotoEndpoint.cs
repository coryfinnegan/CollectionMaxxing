using System.Buffers;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Scanning.Photo;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>What <c>POST …/candidates/{cid}/photo</c> takes: one multipart part named <c>file</c>. (Only describes the body for the contract; the endpoint reads the stream itself.)</summary>
public sealed class CandidatePhotoForm
{
    /// <summary>A JPEG crop of the item, at most 4 MB and 4096 pixels a side.</summary>
    public IFormFile? File { get; init; }
}

/// <summary>
/// <c>POST /api/v1/scan-sessions/{id}/candidates/{cid}/photo</c> ("Use my photo"). The body is read straight from the request into memory,
/// at most 4 MB of it, so the uploaded original is never spooled to a temporary file on disk: only the cleaned copy is ever stored.
/// The route is not bound to a form model, so the framework's antiforgery check (for cookie forms) does not apply; what protects the write is
/// what protects every other write: the bearer token, or the session cookie together with the <c>X-CSRF</c> header (<c>CsrfMiddleware</c>).
/// </summary>
internal static class CandidatePhotoEndpoint
{
    /// <summary>The name of the multipart part that holds the picture.</summary>
    public const string PartName = "file";

    /// <summary>Room for the multipart framing around a 4 MB file.</summary>
    private const int Overhead = 64 * 1024;

    private const int MaxRequestBytes = JpegSanitizer.MaxBytes + Overhead;

    public static async Task<IResult> UploadAsync(
        Guid id, Guid cid, HttpRequest request, UploadCandidatePhoto.Handler handler, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemResults.Problem(
                StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", "Send the picture as multipart/form-data with a part named \"file\".", "scan.photo_not_multipart");
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200)
        {
            return Bad("scan.photo_no_file", "The upload has no multipart boundary.");
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
                    return Bad("scan.photo_one_file", "Send exactly one file.");
                }

                file = await ReadCappedAsync(section.Body, cancellationToken);
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
            return Bad("scan.photo_malformed", "That upload couldn't be read as multipart/form-data.");
        }

        if (file is null)
        {
            return Bad("scan.photo_no_file", "Send the picture as a multipart part named \"file\".");
        }

        var result = await handler.HandleAsync(new UploadCandidatePhoto.Command(id, cid, file), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    /// <summary>Reads at most <see cref="JpegSanitizer.MaxBytes"/> bytes. Null when there is more: the rest is never read.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > JpegSanitizer.MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    private static IResult Bad(string code, string detail) => ProblemResults.Problem(StatusCodes.Status400BadRequest, "Bad request", detail, code);

    private static IResult TooLarge() => ProblemResults.Problem(
        StatusCodes.Status413PayloadTooLarge, "Payload too large", "That picture is over 4 MB. Send a smaller crop.", "scan.photo_too_large");
}
