using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Events;

public class ScanEventsTests : ScanTest
{
    public ScanEventsTests(ApiFixture api)
        : base(api)
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        ConfigureServices = services => services.AddSingleton(new ScanEventOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            Heartbeat = TimeSpan.FromMilliseconds(250),
        });
    }

    private static string EventsUrl(Guid sessionId) => SessionUrl(sessionId) + "/events";

    [Fact]
    public async Task A_client_sees_candidate_added_then_candidate_updated_for_a_slow_code()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(5101);
        var slow = true;
        Products.Behaviour = async (_, ct) =>
        {
            if (slow)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }

            return ProductLookupResult.Found(ScanData.Product("Wave Race 64", code));
        };
        Games.Results.Add(ScanData.Game("Wave Race 64"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await using var events = await SseClient.ConnectAsync(editor, EventsUrl(sessionId));
        events.StatusCode.ShouldBe(HttpStatusCode.OK);
        events.ContentType.ShouldBe("text/event-stream");

        var candidates = await ScanAsync(editor, sessionId, code);
        Resolution(candidates[0]).ShouldBe("pending");
        var added = (await events.ReadUntilAsync("candidate.added")).Last();
        added.Id.ShouldBe("1");
        added.Seq.ShouldBe(1);
        added.Data!.Value.GetProperty("resolution").GetString().ShouldBe("pending");

        slow = false;
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResolvePendingCandidate>().HandleAsync(
                new SubmitBarcodes.ResolveJob(library.Id, sessionId, Id(candidates[0]), library.EditorId), CancellationToken.None);
        }

        var updated = (await events.ReadUntilAsync(
            "candidate.updated", e => e.Data!.Value.GetProperty("resolution").GetString() == "matched")).Last();
        updated.Data!.Value.GetProperty("id").GetGuid().ShouldBe(Id(candidates[0]));
        updated.Data!.Value.GetProperty("resolution").GetString().ShouldBe("matched");
    }

    [Fact]
    public async Task Reconnecting_with_last_event_id_replays_only_what_was_missed()
    {
        var library = await CreateLibraryAsync();
        var codes = new[] { ScanData.Upc(5111), ScanData.Upc(5112), ScanData.Upc(5113) };
        foreach (var code in codes)
        {
            await CatalogAsync($"Game {code}", code);
        }

        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, codes[0], codes[1]);

        string lastId;
        await using (var first = await SseClient.ConnectAsync(editor, EventsUrl(sessionId)))
        {
            var seen = await first.ReadUntilAsync("session.status");
            // Events come in change order, which is not always scan order (two codes resolve at once).
            seen.Where(e => e.Name == "candidate.added").Select(e => e.Seq).Order().ShouldBe([1, 2]);

            // The id is the session's change counter (SCAN-1d): the highest changeSeq shown, which a resolve update also moves.
            lastId = seen.Last().Id!;
            lastId.ShouldBe(seen.Where(e => e.Name != "session.status").Max(e => e.Data!.Value.GetProperty("changeSeq").GetInt32()).ToString());
        }

        await ScanAsync(editor, sessionId, codes[2]); // scanned while the phone was away

        await using var second = await SseClient.ConnectAsync(editor, EventsUrl(sessionId), lastId);
        var replayed = await second.ReadUntilAsync("session.status");
        // session.counters is state like session.status: the reconnected client hears the current counters, not a replay (#342).
        replayed.Select(e => e.Name).ShouldBe(["candidate.added", "session.counters", "session.status"]);
        replayed[0].Seq.ShouldBe(3);
        int.Parse(replayed[0].Id!).ShouldBeGreaterThan(int.Parse(lastId));
        replayed[0].Id.ShouldBe(replayed[0].Data!.Value.GetProperty("changeSeq").GetInt32().ToString());
    }

    [Fact]
    public async Task The_stream_sends_a_heartbeat_comment_and_ends_when_the_session_does()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(5121);
        await CatalogAsync("Pilotwings 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, code);
        await using var events = await SseClient.ConnectAsync(editor, EventsUrl(sessionId));
        var first = await events.ReadUntilAsync("session.status");
        first.Last().Data!.Value.GetProperty("status").GetString().ShouldBe("open");

        SseEvent? beat;
        do
        {
            beat = await events.NextAsync();
        }
        while (beat is { IsComment: false });
        beat.ShouldNotBeNull().Raw.ShouldStartWith(":");

        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var rest = await events.ReadUntilAsync("session.status", e => e.Data!.Value.GetProperty("status").GetString() == "committed");
        rest.Last().Data!.Value.GetProperty("status").GetString().ShouldBe("committed");
        while (await events.NextAsync() is { })
        {
            // drain anything left; the server then closes the stream
        }
    }

    [Fact]
    public async Task A_client_of_another_library_or_another_member_gets_404()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        using var stranger = As(other, LibraryRole.Owner);
        using var owner = As(library, LibraryRole.Owner); // same library, not the one who started it
        foreach (var client in new[] { stranger, owner })
        {
            await using var events = await SseClient.ConnectAsync(client, EventsUrl(sessionId));
            events.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var anonymous = Factory.CreateClient();
        await using var none = await SseClient.ConnectAsync(anonymous, EventsUrl(sessionId));
        none.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
