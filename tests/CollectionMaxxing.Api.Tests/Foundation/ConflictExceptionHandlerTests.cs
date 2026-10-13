using CollectionMaxxing.Api.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;

namespace CollectionMaxxing.Api.Tests.Foundation;

/// <summary>
/// The database has the last word. Two requests racing past a domain check (a delete against an insert, two edits of one
/// row) reach it and are refused there; that must read as the 409 it is, never as a server error.
/// </summary>
public class ConflictExceptionHandlerTests
{
    private readonly IProblemDetailsService _problems = Substitute.For<IProblemDetailsService>();

    public ConflictExceptionHandlerTests()
    {
        _problems.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(true);
    }

    private static DbUpdateException Violation(string sqlState) =>
        new("save failed", new PostgresException("failed", "ERROR", "ERROR", sqlState));

    [Fact]
    public async Task A_foreign_key_violation_becomes_a_409_problem()
    {
        var context = new DefaultHttpContext();

        var handled = await new ForeignKeyViolationExceptionHandler(_problems)
            .TryHandleAsync(context, Violation(PostgresErrorCodes.ForeignKeyViolation), CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        await _problems.Received(1).TryWriteAsync(Arg.Is<ProblemDetailsContext>(p =>
            p.ProblemDetails.Status == 409 && p.ProblemDetails.Type == ProblemResults.TypeFor("conflict.foreign_key")));
    }

    [Fact]
    public async Task A_unique_violation_is_left_to_the_unique_handler()
    {
        var handled = await new ForeignKeyViolationExceptionHandler(_problems)
            .TryHandleAsync(new DefaultHttpContext(), Violation(PostgresErrorCodes.UniqueViolation), CancellationToken.None);

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task Exceptions_that_are_not_database_errors_are_not_a_foreign_key_violation()
    {
        var handled = await new ForeignKeyViolationExceptionHandler(_problems)
            .TryHandleAsync(new DefaultHttpContext(), new InvalidOperationException("boom"), CancellationToken.None);

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_stale_save_becomes_a_409_problem()
    {
        var context = new DefaultHttpContext();

        var handled = await new ConcurrentUpdateExceptionHandler(_problems)
            .TryHandleAsync(context, new DbUpdateConcurrencyException("someone else changed it"), CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        await _problems.Received(1).TryWriteAsync(Arg.Is<ProblemDetailsContext>(p =>
            p.ProblemDetails.Type == ProblemResults.TypeFor("conflict.concurrent_update")));
    }

    [Fact]
    public async Task Other_exceptions_are_not_a_stale_save()
    {
        var handled = await new ConcurrentUpdateExceptionHandler(_problems)
            .TryHandleAsync(new DefaultHttpContext(), new InvalidOperationException("boom"), CancellationToken.None);

        handled.ShouldBeFalse();
    }
}
