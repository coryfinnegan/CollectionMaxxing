using Microsoft.AspNetCore.Diagnostics;

namespace CollectionMaxxing.Api.Auth;

/// <summary>The session record could not be checked (the database failed). The request fails closed; the cookie is left alone.</summary>
public sealed class SessionStoreUnavailableException(Exception inner) : Exception("The session could not be checked.", inner);

/// <summary>Answers <see cref="SessionStoreUnavailableException"/> as a 503 Problem Details with <c>Retry-After</c>.</summary>
public sealed class SessionStoreUnavailableExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not SessionStoreUnavailableException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = "5";
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Temporarily unavailable",
                Detail = "Try again in a moment. You are still signed in.",
                Type = Problems.ProblemResults.TypeFor("auth.session_check_unavailable"),
            },
        });
    }
}
