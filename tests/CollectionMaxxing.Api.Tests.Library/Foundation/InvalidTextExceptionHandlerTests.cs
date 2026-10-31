using CollectionMaxxing.Api.Problems;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;

namespace CollectionMaxxing.Api.Tests.Foundation;

public class InvalidTextExceptionHandlerTests
{
    private readonly IProblemDetailsService _problems = Substitute.For<IProblemDetailsService>();

    public InvalidTextExceptionHandlerTests()
    {
        _problems.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(true);
    }

    private static PostgresException Postgres(string sqlState) => new("rejected", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData(PostgresErrorCodes.CharacterNotInRepertoire)]
    [InlineData(PostgresErrorCodes.UntranslatableCharacter)]
    public async Task Text_the_database_cannot_store_is_a_400_whether_a_query_or_a_save_hit_it(string sqlState)
    {
        foreach (Exception exception in new Exception[] { Postgres(sqlState), new DbUpdateException("save failed", Postgres(sqlState)) })
        {
            var context = new DefaultHttpContext();

            var handled = await new InvalidTextExceptionHandler(_problems).TryHandleAsync(context, exception, CancellationToken.None);

            handled.ShouldBeTrue();
            context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        }
    }

    [Fact]
    public async Task Any_other_database_failure_is_left_to_the_default_handler()
    {
        var handled = await new InvalidTextExceptionHandler(_problems)
            .TryHandleAsync(new DefaultHttpContext(), new DbUpdateException("save failed", Postgres(PostgresErrorCodes.ForeignKeyViolation)), CancellationToken.None);

        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task Exceptions_that_are_not_database_errors_are_left_to_the_default_handler()
    {
        var handled = await new InvalidTextExceptionHandler(_problems)
            .TryHandleAsync(new DefaultHttpContext(), new InvalidOperationException("boom"), CancellationToken.None);

        handled.ShouldBeFalse();
    }
}
