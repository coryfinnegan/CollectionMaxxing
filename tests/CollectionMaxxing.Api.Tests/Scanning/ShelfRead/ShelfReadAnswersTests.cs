using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// The answers of POST …/snaps/{snapId}:read-shelf, one for each case of the table in the issue. A fake reader stands in for the model; pictures are drawn for the test.
public class ShelfReadAnswersTests(ApiFixture api) : ShelfReadTest(api)
{
    /// <summary>The most bytes the lines part may hold (<c>ShelfReadEndpoint.MaxLinesBytes</c>).</summary>
    private const int LinesCap = 256 * 1024;

    private async Task<(HttpClient Client, Guid SessionId, Guid SnapId)> ArrangeAsync(string shotKind = "shelf_segment")
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(9301));
        await CatalogAsync("Wave Race 64", ScanData.Upc(9302));
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);
        var snap = await IdentifyShelfSnapAsync(client, sessionId, shotKind, Read(title: "Star Fox 64", kind: "game", year: 1996));
        Reader.Answer = _ => ScriptedReader.Found("Wave Race 64");
        return (client, sessionId, snap);
    }

    private async Task<DateTimeOffset?> ServerReadAtAsync(Guid snapId)
    {
        await using var context = Db.CreateContext();
        return (await context.ScanSnaps.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == snapId)).ServerReadAt;
    }

    [Fact]
    public async Task No_reader_set_up_answers_reader_off_and_touches_neither_the_reader_nor_the_still_maker_nor_the_queue()
    {
        NoReader();
        Settings["ShelfReader:QueueWaitSeconds"] = "1";
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        using var turn = await Factory.Services.GetRequiredService<ShelfReadGate>().EnterAsync(CancellationToken.None);
        turn.ShouldNotBeNull();

        var response = await ReadShelfAsync(client, sessionId, snap);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).ShouldBe("scan.reader_off");
        Reader.Calls.ShouldBe(0);
        Stills.Calls.ShouldBe(0);
        (await ServerReadAtAsync(snap)).ShouldBeNull();
    }

    [Theory]
    [InlineData(ShelfReadStatus.Unavailable)]
    [InlineData(ShelfReadStatus.TimedOut)]
    public async Task An_unavailable_or_timed_out_reader_answers_503_with_Retry_After_and_changes_nothing(ShelfReadStatus status)
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        Reader.Answer = _ => ShelfReadResult.Empty(status);

        var response = await ReadShelfAsync(client, sessionId, snap);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
        (await ProblemCodeAsync(response)).ShouldBe("scan.reader_unavailable");
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
        (await ServerReadAtAsync(snap)).ShouldBeNull("the snap can be sent again");

        Reader.Answer = _ => ScriptedReader.Found("Wave Race 64");
        (await CandidatesOfAsync(await ReadShelfAsync(client, sessionId, snap))).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_reader_that_turns_out_to_be_off_after_all_answers_reader_off()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        Reader.Answer = _ => ShelfReadResult.Empty(ShelfReadStatus.Off);

        var response = await ReadShelfAsync(client, sessionId, snap);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).ShouldBe("scan.reader_off");
    }

    [Theory]
    [InlineData("single_cover")]
    [InlineData("loose_item")]
    public async Task A_single_cover_or_a_loose_item_is_not_a_shelf(string shotKind)
    {
        var (client, sessionId, snap) = await ArrangeAsync(shotKind);
        using var _ = client;

        var response = await ReadShelfAsync(client, sessionId, snap);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).ShouldBe("scan.not_a_shelf");
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_closed_session_is_refused()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        (await client.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await ReadShelfAsync(client, sessionId, snap);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(response)).ShouldBe("scan.session_closed");
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_snap_identify_has_not_answered_yet_is_not_found()
    {
        var (client, sessionId, _) = await ArrangeAsync();
        using var _1 = client;

        var response = await ReadShelfAsync(client, sessionId, Guid.NewGuid());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_snap_of_another_session_is_not_found()
    {
        var (client, _, snap) = await ArrangeAsync();
        using var _1 = client;
        var library = await CreateLibraryAsync();
        using var other = As(library, LibraryRole.Editor);
        var otherSession = await StartSessionWithoutAutoCommitAsync(other, library.Id);

        var response = await ReadShelfAsync(other, otherSession, snap);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_picture_that_is_not_a_JPEG_is_415()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;

        var response = await ReadShelfAsync(client, sessionId, snap, picture: JpegFixtures.Png());

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_picture_over_4_MB_is_413()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;

        var response = await ReadShelfAsync(client, sessionId, snap, picture: JpegFixtures.Oversize(5 * 1024 * 1024));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_picture_over_4096_pixels_a_side_is_413()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;

        var response = await ReadShelfAsync(client, sessionId, snap, picture: JpegFixtures.WithSize(4097, 100));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_request_with_no_file_part_is_400()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        using var form = Form(picture: null, lines: ["A LINE"]);

        var response = await client.PostAsync(ReadShelfUrl(sessionId, snap), form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).ShouldBe("scan.photo_no_file");
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_picture_sent_under_another_part_name_is_400()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        using var form = Form(JpegFixtures.Plain(), fileField: "picture");

        var response = await client.PostAsync(ReadShelfUrl(sessionId, snap), form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_body_that_is_not_multipart_is_415()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;

        var response = await client.PostAsync(ReadShelfUrl(sessionId, snap), Json(new { }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Lines_that_are_not_a_JSON_array_of_strings_are_400()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        using var form = Form(JpegFixtures.Plain());
        form.Add(new StringContent("{\"not\":\"a list\"}"), "lines");

        var response = await client.PostAsync(ReadShelfUrl(sessionId, snap), form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).ShouldBe("scan.shelf_lines_invalid");
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Lines_over_the_size_cap_are_400()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        var line = new string('x', 1000);
        var lines = Enumerable.Repeat(line, (LinesCap / line.Length) + 2).ToArray(); // a valid list, just too long

        var response = await ReadShelfAsync(client, sessionId, snap, lines);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).ShouldBe("scan.shelf_lines_invalid");
        Reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Lines_just_under_the_size_cap_are_read()
    {
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        var line = new string('x', 1000);
        var lines = Enumerable.Repeat(line, (LinesCap / (line.Length + 3)) - 1).ToArray();

        (await ReadShelfAsync(client, sessionId, snap, lines)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Two_requests_for_the_same_snap_at_once_store_the_rows_once_and_answer_the_same_ids()
    {
        Settings["ShelfReader:QueueWaitSeconds"] = "30";
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Reader.Hold = (call, token) => call == 1 ? release.Task.WaitAsync(token) : Task.CompletedTask;
        var first = ReadShelfAsync(client, sessionId, snap);
        var until = DateTime.UtcNow.AddSeconds(20);
        while (Reader.Calls == 0)
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the first read never reached the reader");
            await Task.Delay(20);
        }

        var second = ReadShelfAsync(client, sessionId, snap);
        await Task.Delay(300);
        release.SetResult();

        var one = await CandidatesOfAsync(await first.WaitAsync(TimeSpan.FromSeconds(20)));
        var two = await CandidatesOfAsync(await second.WaitAsync(TimeSpan.FromSeconds(20)));

        one.Count.ShouldBe(1);
        two.Select(Id).ShouldBe(one.Select(Id));
        Reader.Calls.ShouldBeInRange(1, 2, "the twin may read too while the first grounds its finds; only one keeps rows");
        (await CountCandidatesAsync(sessionId)).ShouldBe(2, "the phone's row and the server's, once");
    }

    [Fact]
    public async Task The_request_body_is_never_buffered_to_a_file_while_the_reader_reads()
    {
        AddServices(services => services.AddHttpContextAccessor());
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;
        Type? bodyType = null;
        Reader.Hold = (_, _) =>
        {
            bodyType = Factory.Services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext?.Request.Body.GetType();
            return Task.CompletedTask;
        };

        (await ReadShelfAsync(client, sessionId, snap)).StatusCode.ShouldBe(HttpStatusCode.OK);

        bodyType.ShouldNotBeNull("the read ran inside the request");
        bodyType.ShouldNotBe(typeof(Microsoft.AspNetCore.WebUtilities.FileBufferingReadStream));
    }

    [Fact]
    public async Task Too_many_calls_in_a_minute_are_429()
    {
        Settings["Security:Limits:ShelfRead:Permits"] = "2";
        var (client, sessionId, snap) = await ArrangeAsync();
        using var _ = client;

        (await ReadShelfAsync(client, sessionId, snap)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadShelfAsync(client, sessionId, snap)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var third = await ReadShelfAsync(client, sessionId, snap);

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.ShouldNotBeNull();
    }
}
