using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CollectionMaxxing.Api.Problems;

/// <summary>
/// The database has the last word on uniqueness. If two requests race past the domain check, the second one hits
/// a unique index; report that as the 409 it is, not as a server error.
/// </summary>
public sealed class UniqueViolationExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } })
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = "That conflicts with something that already exists.",
                Type = ProblemResults.TypeFor("conflict.unique"),
            },
        });
    }
}
