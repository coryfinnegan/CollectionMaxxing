using Microsoft.AspNetCore.Diagnostics;

namespace CollectionMaxxing.Api.Problems;

/// <summary>
/// A form body with an illegal character in it (a NUL after percent-decoding, say) makes ASP.NET throw while a handler reads
/// <c>/connect/token</c> or <c>/connect/revoke</c>. That is the caller's mistake: report it as the 400 it is, not as a server error.
/// </summary>
public sealed class MalformedFormExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // ASP.NET's form reader throws InvalidDataException for a bad form body; matching the type and a form content type
        // (not the message text, which can change between releases) keeps this from swallowing an unrelated InvalidDataException.
        if (exception is not InvalidDataException || !httpContext.Request.HasFormContentType)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad request",
                Detail = "The form has a character that isn't allowed.",
                Type = ProblemResults.TypeFor("request.malformed_form"),
            },
        });
    }
}
