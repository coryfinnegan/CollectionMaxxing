using System.Diagnostics;
using System.Text.Json;
using CollectionMaxxing.Api.Http;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Application.Scanning.ShelfReading;
using CollectionMaxxing.Application.ShelfReading;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace CollectionMaxxing.Api.Scanning.ShelfRead;

/// <summary>What <c>POST …/snaps/{snapId}:read-shelf</c> takes. (Only describes the body for the contract; the endpoint reads the stream itself.)</summary>
public sealed class ShelfReadForm
{
    /// <summary>A JPEG of the shelf, at most 4 MB and 4096 pixels a side.</summary>
    public IFormFile? File { get; init; }

    /// <summary>The text lines Vision read in the shot, as a JSON array of strings. Optional, and checked, but not used: the server's reads need no print to stand.</summary>
    public string? Lines { get; init; }
}

/// <summary>
/// <c>POST /api/v1/scan-sessions/{id}/snaps/{snapId}:read-shelf</c> (ADR-033): the shelf picture of a snap <c>identify</c> has answered, read by the
/// server's own reader, and the snap settled server first (<see cref="PcShelfUpgrade"/>): the titles it finds that the scan lacks added as unconfirmed
/// rows, and the phone's rows the rule leaves out skipped (skip reason <c>server_did_not_see</c>) unless the person acted on them. The picture is read straight from the request into
/// memory, at most 4 MB, as the photo upload does: it is never spooled to disk, never stored, and gone when the request ends. The route is not bound
/// to a form model, so no antiforgery token applies; what protects the write is what protects every other write (the bearer token, or the session
/// cookie with the <c>X-CSRF</c> header).
/// </summary>
internal static class ShelfReadEndpoint
{
    public const string FilePartName = "file";

    public const string LinesPartName = "lines";

    /// <summary>A shelf's print is a few hundred short lines; more than this is not a list of lines.</summary>
    public const int MaxLinesBytes = 256 * 1024;

    /// <summary>Room for the multipart framing and the lines around a 4 MB file.</summary>
    private const int Overhead = 320 * 1024;

    private const int MaxRequestBytes = JpegSanitizer.MaxBytes + Overhead;

    private const int RetryAfterBusySeconds = 10;

    private const int RetryAfterUnavailableSeconds = 20;

    public static async Task<IResult> HandleAsync(
        Guid id,
        Guid snapId,
        HttpRequest request,
        IShelfReaderSetup setup,
        ReadShelfSnap.Handler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("CollectionMaxxing.Api.Scanning.ShelfRead.ShelfReadEndpoint");
        var clock = Stopwatch.StartNew();

        // Nothing to read with: say so before the picture is read off the wire.
        if (!setup.IsSetUp)
        {
            return Answer(logger, ShelfReadErrors.ReaderOff.ToProblem(), ShelfReadErrors.ReaderOff.Code, StatusCodes.Status409Conflict, 0, clock);
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(logger, clock, StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", "Send the picture as multipart/form-data with a part named \"file\".", "scan.photo_not_multipart");
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200)
        {
            return Refuse(logger, clock, StatusCodes.Status400BadRequest, "Bad request", "The upload has no multipart boundary.", "scan.photo_no_file");
        }

        if (request.ContentLength > MaxRequestBytes)
        {
            return TooLarge(logger, clock);
        }

        // Kestrel enforces the same cap for a body sent without a length. (Not every server lets it be changed: then the read cap below is the guard.)
        var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxRequestBytes;
        }

        byte[]? file = null;
        string? linesText = null;
        var linesTooLong = false;
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
                if (name == LinesPartName && linesText is null && !linesTooLong)
                {
                    // Lines longer than the cap are as invalid as a malformed list.
                    linesText = await BoundedBody.ReadTextAsync(section.Body, MaxLinesBytes, cancellationToken);
                    linesTooLong = linesText is null;
                    continue; // the phone sends the lines as a field or a file part; both read the same
                }

                if (!disposition.IsFileDisposition() || name != FilePartName)
                {
                    continue; // not the picture: ignored, and drained by the next read
                }

                if (file is not null)
                {
                    return Refuse(logger, clock, StatusCodes.Status400BadRequest, "Bad request", "Send exactly one file.", "scan.photo_one_file");
                }

                // A part has no declared length, so it is read in chunks. Null is a picture over the cap.
                file = await BoundedBody.ReadAsync(section.Body, JpegSanitizer.MaxBytes, declaredLength: null, cancellationToken);
                if (file is null)
                {
                    return TooLarge(logger, clock);
                }
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(logger, clock);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // Cut short or not multipart at all (a client that went away never reads this answer). Comes after the 413 above:
            // BadHttpRequestException is an IOException too.
            return Refuse(logger, clock, StatusCodes.Status400BadRequest, "Bad request", "That upload couldn't be read as multipart/form-data.", "scan.photo_malformed");
        }

        if (file is null)
        {
            return Refuse(logger, clock, StatusCodes.Status400BadRequest, "Bad request", "Send the picture as a multipart part named \"file\".", "scan.photo_no_file");
        }

        // A sound JPEG of a size we accept; the bytes sent on are the original, because the still maker turns it upright and strips it itself.
        var checkedJpeg = JpegSanitizer.Clean(file);
        if (!checkedJpeg.IsSuccess)
        {
            var error = checkedJpeg.Error!;
            if (error.Code == JpegSanitizer.NotJpeg.Code)
            {
                return Refuse(logger, clock, StatusCodes.Status415UnsupportedMediaType, "Unsupported media type", error.Message, error.Code);
            }

            if (error.Code == JpegSanitizer.TooLarge.Code)
            {
                return TooLarge(logger, clock);
            }

            return Answer(logger, error.ToProblem(), error.Code, StatusCodes.Status400BadRequest, 0, clock);
        }

        // The lines are still checked, so a client sending a broken list hears about it; the server-first rule (#814) needs no print.
        if (linesTooLong || !LinesAreValid(linesText))
        {
            return Refuse(logger, clock, StatusCodes.Status400BadRequest, "Bad request", "The lines must be a JSON array of strings.", "scan.shelf_lines_invalid");
        }

        var result = await handler.HandleAsync(new ReadShelfSnap.Command(id, snapId, file), cancellationToken);
        if (result.IsSuccess)
        {
            LogOutcome(logger, "ok", StatusCodes.Status200OK, result.Value.ItemsRead, clock);
            return TypedResults.Ok(result.Value.Candidates);
        }

        var refused = result.Error!;
        if (ShelfReadErrors.IsTemporary(refused))
        {
            var busy = refused.Code == ShelfReadErrors.ReaderBusy.Code;
            request.HttpContext.Response.Headers.RetryAfter = (busy ? RetryAfterBusySeconds : RetryAfterUnavailableSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            LogOutcome(logger, refused.Code, StatusCodes.Status503ServiceUnavailable, 0, clock);
            return ProblemResults.Problem(StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable", refused.Message, refused.Code);
        }

        var problem = refused.ToProblem();
        LogOutcome(logger, refused.Code, StatusCodeOf(refused), 0, clock);
        return problem;
    }

    private static int StatusCodeOf(Domain.Common.DomainError error) => error.Kind switch
    {
        Domain.Common.ErrorKind.NotFound => StatusCodes.Status404NotFound,
        Domain.Common.ErrorKind.Conflict => StatusCodes.Status409Conflict,
        Domain.Common.ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>True when the part is absent or empty, or a JSON array of strings; false for anything else.</summary>
    private static bool LinesAreValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        try
        {
            return JsonSerializer.Deserialize<string?[]>(text) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IResult Answer(ILogger logger, IResult problem, string code, int status, int items, Stopwatch clock)
    {
        LogOutcome(logger, code, status, items, clock);
        return problem;
    }

    private static IResult Refuse(ILogger logger, Stopwatch clock, int status, string title, string detail, string code)
    {
        LogOutcome(logger, code, status, 0, clock);
        return ProblemResults.Problem(status, title, detail, code);
    }

    private static IResult TooLarge(ILogger logger, Stopwatch clock) => Refuse(
        logger, clock, StatusCodes.Status413PayloadTooLarge, "Payload too large", "That picture is over 4 MB or 4096 pixels. Send a smaller one.", "scan.photo_too_large");

    /// <summary>One line per request: the problem code (or ok), the status, how many items the reader read and how long it took. Never a title, a line of print or any bytes.</summary>
    private static void LogOutcome(ILogger logger, string code, int status, int items, Stopwatch clock)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Shelf read ended {ProblemCode} (HTTP {Status}) with {Items} items read in {Milliseconds} ms.", code, status, items, clock.ElapsedMilliseconds);
        }
    }
}
