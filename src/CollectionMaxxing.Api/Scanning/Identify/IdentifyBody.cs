using System.Runtime.ExceptionServices;
using System.Text.Json;
using CollectionMaxxing.Api.Http;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Api.Security;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Scanning.Identify;

/// <summary>Why an identify body is refused: the problem code (for the log), the problem to answer with and, for a bad body, where in it.</summary>
internal sealed record IdentifyRefusal(string Code, IResult Problem, string? Field = null);

/// <summary>
/// What the body of an identify call turned out to be: the request, the reason it is refused, or the failure of reading it (exactly
/// one is set), and what the call costs the identify group in permits (<see cref="Permits"/>).
/// </summary>
internal sealed record IdentifyBodyOutcome(
    IdentifyRequest? Request, IdentifyRefusal? Refusal, int Permits, ExceptionDispatchInfo? ReadFailure = null);

/// <summary>
/// The body of <c>POST …/identify</c>, read and parsed once per call. The limiter needs the item count before the endpoint runs
/// (the call costs one permit per item, and it has to be paid before the session filters answer), and the endpoint needs the
/// request itself. Whichever asks first reads the body, bounded, and parses it; the other gets the same outcome from the
/// request, so the body is neither buffered nor parsed a second time.
/// </summary>
internal static class IdentifyBody
{
    /// <summary>The most a body may weigh: 120 items of up to 16 KB would be more, but a real read is a few hundred bytes.</summary>
    public const int MaxBytes = 256 * 1024;

    public const string TooLargeCode = "scan.body_too_large";

    private static readonly object CacheKey = new();

    /// <summary>The rate-limit group of the identify call, which costs one permit per item the body carries.</summary>
    public static RateLimitGroup RateLimit { get; } = new(nameof(SecurityOptions.LimitOptions.Identify), PermitsAsync);

    /// <summary>
    /// The call's outcome, in the order the endpoint judges a body: a body that is not JSON, one over <see cref="MaxBytes"/> (by
    /// its declared length, or once read), one that does not parse. No image is ever accepted on this path.
    /// </summary>
    public static async ValueTask<IdentifyBodyOutcome> ReadAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(CacheKey, out var known) && known is IdentifyBodyOutcome outcome)
        {
            return outcome;
        }

        outcome = await ParseAsync(http);
        http.Items[CacheKey] = outcome;
        return outcome;
    }

    /// <summary>
    /// What the call costs the identify group: one permit per item the request carries, at least one. A body that does not bind
    /// still costs the items it claims (the elements of its <c>items</c> array, counted from the bytes), so a malformed batch costs
    /// what it says it is; one that cannot be counted, cannot be read to its end, or is refused for another reason costs one, as a
    /// call that does nothing still has to come from somewhere; one that is too large costs none, so it is answered 413 at once
    /// whatever the caller's allowance.
    /// </summary>
    public static async ValueTask<int> PermitsAsync(HttpContext http) => (await ReadAsync(http)).Permits;

    private static async ValueTask<IdentifyBodyOutcome> ParseAsync(HttpContext http)
    {
        var request = http.Request;
        if (!IsJson(request.ContentType))
        {
            return Refused(
                "scan.json_required",
                ProblemResults.Problem(
                    StatusCodes.Status415UnsupportedMediaType,
                    "Unsupported media type",
                    "Send what was read as application/json. A picture is never sent to the server on this path.",
                    "scan.json_required"));
        }

        if (request.ContentLength > MaxBytes)
        {
            return Refused(TooLargeCode, TooLarge());
        }

        byte[]? body;
        try
        {
            body = await BoundedBody.ReadAsync(request.Body, MaxBytes, request.ContentLength, http.RequestAborted);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // Fewer bytes arrived than the call declared (Kestrel's BadHttpRequestException is an IOException), or the connection
            // broke. The call is charged one permit and the endpoint fails with this same exception, as it does when it is the one
            // reading the body. An abort of the request (OperationCanceledException) is not caught and ends the call as it always did.
            return new IdentifyBodyOutcome(null, null, 1, ExceptionDispatchInfo.Capture(exception));
        }

        if (body is null)
        {
            return Refused(TooLargeCode, TooLarge());
        }

        var json = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>();
        try
        {
            return JsonSerializer.Deserialize<IdentifyRequest>(body, BoxTolerantJson.Of(json.Value.SerializerOptions)) is { } parsed
                ? new IdentifyBodyOutcome(parsed, null, Math.Max(1, parsed.Items?.Count ?? 0))
                : Refused("scan.body_invalid", BadBody());
        }
        catch (JsonException exception)
        {
            // The path inside the body names the member that could not be read (never its value).
            return Refused("scan.body_invalid", BadBody(), exception.Path, ClaimedItems(body));
        }
    }

    /// <summary>
    /// The length of the body's <c>items</c> array (at least one), for a body that did not bind: counted from the bytes, one element
    /// after the other, without building any. A name is matched without regard to case, and the last <c>items</c> member wins, as in
    /// the binding of a body that does. A body that is not an object, has no array there, or is not JSON to its end, claims one.
    /// </summary>
    private static int ClaimedItems(byte[] body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return 1;
            }

            var claimed = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isItems = string.Equals(reader.GetString(), "items", StringComparison.OrdinalIgnoreCase);
                reader.Read();
                if (isItems && reader.TokenType == JsonTokenType.StartArray)
                {
                    claimed = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        claimed++;
                        reader.Skip();
                    }
                }
                else
                {
                    claimed = isItems ? 0 : claimed; // an items member that is no array claims nothing, and it is the one that wins
                    reader.Skip();
                }
            }

            reader.Read(); // anything but white space after the object is not JSON
            return Math.Max(1, claimed);
        }
        catch (JsonException)
        {
            return 1;
        }
    }

    private static IdentifyBodyOutcome Refused(string code, IResult problem, string? field = null, int permits = 1) =>
        new(null, new IdentifyRefusal(code, problem, field), code == TooLargeCode ? 0 : permits);

    private static IResult BadBody() => ProblemResults.Problem(
        StatusCodes.Status400BadRequest, "Bad request", "The body isn't valid JSON for an identify request.", "scan.body_invalid");

    private static bool IsJson(string? contentType) =>
        contentType is not null
        && (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    private static IResult TooLarge() => ProblemResults.Problem(
        StatusCodes.Status413PayloadTooLarge,
        "Payload too large",
        $"An identify request can be at most {MaxBytes / 1024} KB.",
        TooLargeCode);
}
