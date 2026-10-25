using System.Net;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Support;
using Npgsql;

namespace CollectionMaxxing.Api.Tests.Security;

// When the database misbehaves, the session check must not sign people out and must not answer 500.
public class WebSessionOutageTests(ApiFixture api) : InviteTest(api)
{
    [Fact]
    public async Task A_failing_last_seen_write_does_not_fail_the_request_and_logs_without_the_cookie()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var cookie = browser.SessionCookieValue!;
        await ExecuteAsync("""
            create function cm_test_block_last_seen() returns trigger as $$ begin raise exception 'last seen blocked'; end $$ language plpgsql;
            create trigger cm_test_block_last_seen before update of last_seen_at on web_sessions for each row execute function cm_test_block_last_seen();
            """);
        try
        {
            App.Time.Advance(TimeSpan.FromMinutes(5)); // past the once-a-minute write

            var response = await browser.GetAsync("/api/v1/me");

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            App.Logs.Lines.ShouldNotContain(line => line.Contains(cookie));
            App.Logs.Lines.Count(line => line.Contains("last seen", StringComparison.OrdinalIgnoreCase) && line.Contains("session")).ShouldBeGreaterThan(0);
        }
        finally
        {
            // The database is shared by later tests: leave it as it was.
            await ExecuteAsync("drop trigger cm_test_block_last_seen on web_sessions; drop function cm_test_block_last_seen();");
        }
    }

    [Fact]
    public async Task A_database_failure_during_the_session_check_answers_503_with_retry_after_and_keeps_the_cookie()
    {
        var (browser, _) = await SetUpOwnerAsync();
        await ExecuteAsync("alter table web_sessions rename to web_sessions_away");
        try
        {
            var down = await browser.GetAsync("/api/v1/me");

            down.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            down.Headers.RetryAfter.ShouldNotBeNull();
            down.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            down.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeFalse(string.Join(';', cookies ?? []));
        }
        finally
        {
            await ExecuteAsync("alter table web_sessions_away rename to web_sessions");
        }

        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
