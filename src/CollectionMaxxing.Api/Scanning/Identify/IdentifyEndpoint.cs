using System.Text.Json;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Scanning.Identify;

/// <summary>
/// <c>POST /api/v1/scan-sessions/{id}/identify</c>: what the phone read from one picture (docs/04). The body is read here,
/// after the session and role filters have run, and bounded: a stranger's session is 404 whatever the body looks like, and an
/// oversize body or an image is refused with a problem, never a 500. No image is ever accepted on this path.
/// </summary>
internal static class IdentifyEndpoint
{
    /// <summary>The most a body may weigh: 40 items of up to 16 KB would be more, but a real read is a few hundred bytes.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    public static async Task<IResult> HandleAsync(
        Guid id,
        HttpContext http,
        IOptions<JsonOptions> json,
        IdentifyItems.Handler handler,
        CancellationToken cancellationToken)
    {
        var request = http.Request;
        if (!IsJson(request.ContentType))
        {
            return ProblemResults.Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported media type",
                "Send what was read as application/json. A picture is never sent to the server on this path.",
                "scan.json_required");
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            return TooLarge();
        }

        var body = await ReadBoundedAsync(request.Body, cancellationToken);
        if (body is null)
        {
            return TooLarge();
        }

        IdentifyRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<IdentifyRequest>(body, json.Value.SerializerOptions);
        }
        catch (JsonException)
        {
            return ProblemResults.Problem(
                StatusCodes.Status400BadRequest, "Bad request", "The body isn't valid JSON for an identify request.", "scan.body_invalid");
        }

        if (parsed is null)
        {
            return ProblemResults.Problem(
                StatusCodes.Status400BadRequest, "Bad request", "The body isn't valid JSON for an identify request.", "scan.body_invalid");
        }

        var input = new IdentifyInput(
            parsed.SnapId,
            parsed.Trigger,
            parsed.Engine,
            parsed.FellBackFrom,
            parsed.Items?.Select(i => i is null
                ? null!
                : new IdentifyItemInput(
                    i.LocalId, i.SegmentId, i.Text, i.Title, i.Creator, i.Edition, i.Completeness, i.Kind, i.Format, i.Year, i.Platform, i.LocalMatchReleaseId, i.ProductCode,
                    i.Box is { } box ? new ReadBoxInput(box.X, box.Y, box.Width, box.Height) : null))
                .ToList(),
            parsed.ShotKind,
            parsed.Kind ?? IdentifyKind.Shelf,
            parsed.CandidateId,
            parsed.ItemId);

        var result = await handler.HandleAsync(new IdentifyItems.Command(id, input), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static bool IsJson(string? contentType) =>
        contentType is not null
        && (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    private static IResult TooLarge() => ProblemResults.Problem(
        StatusCodes.Status413PayloadTooLarge,
        "Payload too large",
        $"An identify request can be at most {MaxBodyBytes / 1024} KB.",
        "scan.body_too_large");

    /// <summary>The body, or null when it is longer than <see cref="MaxBodyBytes"/>. Reads at most one byte past the limit.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > MaxBodyBytes ? null : buffer.AsSpan(0, total).ToArray();
    }
}
