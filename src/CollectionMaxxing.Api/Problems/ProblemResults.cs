using System.Diagnostics;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Api.Problems;

/// <summary>Builds RFC 9457 Problem Details responses, the only error format this API uses.</summary>
public static class ProblemResults
{
    /// <summary>The <c>type</c> of a problem: a stable URN per error code, e.g. <c>urn:collectionmaxxing:problem:location.cycle</c>.</summary>
    public static string TypeFor(string code) => $"urn:collectionmaxxing:problem:{code}";

    public static IResult Problem(int status, string title, string detail, string? code = null) =>
        TypedResults.Problem(
            statusCode: status,
            title: title,
            detail: detail,
            type: code is null ? null : TypeFor(code),
            extensions: Extensions(code));

    /// <summary>Turns a business-rule failure into 400 (validation), 404 (not found) or 409 (conflict).</summary>
    public static IResult ToProblem(this DomainError error) => error.Kind switch
    {
        ErrorKind.Validation => TypedResults.ValidationProblem(
            new Dictionary<string, string[]> { [error.Field ?? string.Empty] = [error.Message] },
            type: TypeFor(error.Code),
            extensions: Extensions(error.Code)),
        ErrorKind.NotFound => Problem(StatusCodes.Status404NotFound, "Not found", error.Message, error.Code),
        ErrorKind.Conflict => Problem(StatusCodes.Status409Conflict, "Conflict", error.Message, error.Code),
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    private static Dictionary<string, object?> Extensions(string? code)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["traceId"] = Activity.Current?.Id,
        };
        if (code is not null)
        {
            extensions["code"] = code;
        }

        return extensions;
    }
}
