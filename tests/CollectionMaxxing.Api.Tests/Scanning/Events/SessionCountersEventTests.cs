using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Events;

public class SessionCountersEventTests : ScanTest
{
    public SessionCountersEventTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services => services.AddSingleton(new ScanEventOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            Heartbeat = TimeSpan.FromMilliseconds(250),
        });
    }

    private static string EventsUrl(Guid sessionId) => SessionUrl(sessionId) + "/events";

    [Fact]
    public async Task A_client_sees_session_counters_carry_the_counters_and_the_change_seq()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9221);
        await CatalogAsync("Counted Game", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await using var events = await SseClient.ConnectAsync(editor, EventsUrl(sessionId));
        events.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The first look establishes the baseline: an empty session counts zero.
        var baseline = (await events.ReadUntilAsync("session.counters")).Last();
        baseline.Data!.Value.GetProperty("counters").GetProperty("captured").GetInt32().ShouldBe(0);

        await ScanAsync(editor, sessionId, code);

        var updated = (await events.ReadUntilAsync(
            "session.counters", e => e.Data!.Value.GetProperty("counters").GetProperty("captured").GetInt32() == 1)).Last();
        updated.Id.ShouldBe("1");
        updated.Data!.Value.GetProperty("changeSeq").GetInt32().ShouldBe(1);
        // Matched and auto-accepted, but not committed yet: still to review.
        updated.Data!.Value.GetProperty("counters").GetProperty("toReview").GetInt32().ShouldBe(1);
    }
}
