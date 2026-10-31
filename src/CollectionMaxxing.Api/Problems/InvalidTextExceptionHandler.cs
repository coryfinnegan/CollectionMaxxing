using CollectionMaxxing.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CollectionMaxxing.Api.Problems;

/// <summary>
/// The last net for text the database can't store (a NUL, say). The inputs check their own text and name the field; if one is
/// missed, Postgres refuses the value (while saving or while running a query), and that is the caller's mistake, a 400, not a server error.
/// </summary>
public sealed class InvalidTextExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var postgres = exception as PostgresException ?? (exception as DbUpdateException)?.InnerException as PostgresException;
        if (postgres?.SqlState is not (PostgresErrorCodes.CharacterNotInRepertoire or PostgresErrorCodes.UntranslatableCharacter))
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
                Detail = "The text has a character that can't be stored.",
                Type = ProblemResults.TypeFor(TextRules.InvalidCharacterCode),
            },
        });
    }
}
