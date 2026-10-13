using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CollectionMaxxing.Api.Problems;

internal static class ConflictProblems
{
    public static bool IsPostgresError(Exception exception, string sqlState) =>
        exception is DbUpdateException { InnerException: PostgresException postgres } && postgres.SqlState == sqlState;

    public static async ValueTask<bool> WriteAsync(IProblemDetailsService problems, HttpContext httpContext, string code, string detail)
    {
        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = detail,
                Type = ProblemResults.TypeFor(code),
            },
        });
    }
}

/// <summary>
/// A delete that races an insert (deleting a location while an item is being put in it) is refused by a foreign key.
/// Report it as the 409 it is, not as a server error.
/// </summary>
public sealed class ForeignKeyViolationExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken) =>
        ConflictProblems.IsPostgresError(exception, PostgresErrorCodes.ForeignKeyViolation)
        && await ConflictProblems.WriteAsync(
            problems, httpContext, "conflict.foreign_key", "Something else still depends on this, or what it refers to is gone.");
}

/// <summary>Someone else changed the same row between this request's read and its save (the <c>xmin</c> check failed).</summary>
public sealed class ConcurrentUpdateExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken) =>
        exception is DbUpdateConcurrencyException
        && await ConflictProblems.WriteAsync(
            problems, httpContext, "conflict.concurrent_update", "Someone else changed this at the same time. Reload it and try again.");
}
