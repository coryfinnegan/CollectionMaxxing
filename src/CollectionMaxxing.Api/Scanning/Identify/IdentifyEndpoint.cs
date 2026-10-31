using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Api.Scanning.Identify;

/// <summary>
/// <c>POST /api/v1/scan-sessions/{id}/identify</c>: what the phone read from one picture (docs/04). The body is judged here,
/// after the session and role filters have run, though it was read earlier if the limiter asked for the item count
/// (<see cref="IdentifyBody"/>): a stranger's session is 404 whatever the body looks like, and an oversize body or an image is
/// refused with a problem, never a 500. No image is ever accepted on this path.
/// </summary>
internal static class IdentifyEndpoint
{
    public static async Task<IResult> HandleAsync(
        Guid id,
        HttpContext http,
        IdentifyItems.Handler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Every refusal is logged with its problem code (never the body), so a 400 can be explained from the server log.
        var logger = loggerFactory.CreateLogger("CollectionMaxxing.Api.Scanning.Identify.IdentifyEndpoint");
        var outcome = await IdentifyBody.ReadAsync(http);
        if (outcome.Refusal is { } refusal)
        {
            return Refused(logger, refusal.Code, refusal.Problem, refusal.Field);
        }

        var parsed = outcome.Request!;
        var input = new IdentifyInput(
            parsed.SnapId,
            parsed.Trigger,
            parsed.Engine,
            parsed.FellBackFrom,
            parsed.Items?.Select(i => i is null
                ? null!
                : new IdentifyItemInput(
                    i.LocalId, i.SegmentId, i.Text, i.Title, i.Creator, i.Edition, i.Completeness, i.Kind, i.Format, i.Year, i.Platform, i.LocalMatchReleaseId, i.ProductCode,
                    i.Box is { } box ? new ReadBoxInput(box.X, box.Y, box.Width, box.Height) : null,
                    i.Unconfirmed == true))
                .ToList(),
            parsed.ShotKind,
            parsed.Kind ?? IdentifyKind.Shelf,
            parsed.CandidateId,
            parsed.ItemId);

        var result = await handler.HandleAsync(new IdentifyItems.Command(id, input), cancellationToken);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        // Not-found and conflict answers are logged too: any refusal can be asked about.
        var error = result.Error!;
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Identify refused: {ProblemCode} at {Field}.", error.Code, error.Field);
        }

        return error.ToProblem();
    }

    private static IResult Refused(ILogger logger, string code, IResult problem, string? field = null)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Identify refused: {ProblemCode} at {Field}.", code, field);
        }

        return problem;
    }
}
