using CollectionMaxxing.Api.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Npgsql;

namespace CollectionMaxxing.Api.Tests.Foundation;

public class UniqueViolationExceptionHandlerTests
{
    private readonly IProblemDetailsService _problems = Substitute.For<IProblemDetailsService>();

    public UniqueViolationExceptionHandlerTests()
    {
        _problems.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(true);
    }

    [Fact]
    public async Task A_unique_index_violation_that_slips_past_the_domain_check_becomes_a_409()
    {
        var context = new DefaultHttpContext();
        var violation = new DbUpdateException("save failed", new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));

        var handled = await new UniqueViolationExceptionHandler(_problems).TryHandleAsync(context, violation, CancellationToken.None);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Any_other_database_failure_is_left_to_the_default_handler()
    {
        var context = new DefaultHttpContext();
        var other = new DbUpdateException("save failed", new PostgresException("fk", "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation));

        var handled = await new UniqueViolationExceptionHandler(_problems).TryHandleAsync(context, other, CancellationToken.None);

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task Exceptions_that_are_not_database_errors_are_left_to_the_default_handler()
    {
        var handled = await new UniqueViolationExceptionHandler(_problems)
            .TryHandleAsync(new DefaultHttpContext(), new InvalidOperationException("boom"), CancellationToken.None);

        handled.ShouldBeFalse();
    }
}
