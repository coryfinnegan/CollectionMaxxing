using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Events;

// SCAN-1d (issue #105), contract item 5: a candidate that changed while the client was away must come back when it reconnects.
// Before the fix, Last-Event-ID and ?since= were the candidate's position (seq), so a change to a candidate the client already
// had was never replayed.
public class ScanEventReplayTests : ScanTest
{
    public ScanEventReplayTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services => services.AddSingleton(new ScanEventOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            Heartbeat = TimeSpan.FromMilliseconds(250),
        });
    }

    private static string EventsUrl(Guid sessionId) => SessionUrl(sessionId) + "/events";

    private static int ChangeSeq(System.Text.Json.JsonElement candidate) => candidate.GetProperty("changeSeq").GetInt32();

    [Fact]
    public async Task A_candidate_updated_while_the_client_was_disconnected_is_replayed_from_last_event_id()
    {
        var library = await CreateLibraryAsync();
        var (first, second) = (ScanData.Upc(7301), ScanData.Upc(7302));
        await CatalogAsync("Game one", first);
        await CatalogAsync("Game two", second);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, first, second);

        string lastId;
        await using (var connected = await SseClient.ConnectAsync(editor, EventsUrl(sessionId)))
        {
            var seen = await connected.ReadUntilAsync("session.status");
            seen.Count(e => e.Name == "candidate.added").ShouldBe(2);
            lastId = seen.Last().Id!;
        }

        // While the phone was away the person changed the first candidate, which the client had already been shown.
        (await DecideAsync(editor, sessionId, Id(candidates[0]), new { copies = 3 })).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var again = await SseClient.ConnectAsync(editor, EventsUrl(sessionId), lastId);
        var replayed = await again.ReadUntilAsync("session.status");

        var changed = replayed.Where(e => e.Name is "candidate.added" or "candidate.updated").ToList();
        changed.Count.ShouldBe(1, "only what changed since: not the untouched second candidate");
        changed[0].Data!.Value.GetProperty("id").GetGuid().ShouldBe(Id(candidates[0]));
        changed[0].Data!.Value.GetProperty("copies").GetInt32().ShouldBe(3);
        int.Parse(changed[0].Id!).ShouldBeGreaterThan(int.Parse(lastId));
    }

    [Fact]
    public async Task Get_since_returns_candidates_that_changed_after_that_counter_not_only_new_ones()
    {
        var library = await CreateLibraryAsync();
        var (first, second) = (ScanData.Upc(7311), ScanData.Upc(7312));
        await CatalogAsync("Game one", first);
        await CatalogAsync("Game two", second);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, first, second);
        var all = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates").EnumerateArray().ToList();
        var seenUpTo = all.Max(ChangeSeq);

        (await DecideAsync(editor, sessionId, Id(candidates[0]), new { copies = 2 })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var since = (await GetSessionAsync(editor, sessionId, seenUpTo)).GetProperty("candidates").EnumerateArray().ToList();
        since.Count.ShouldBe(1);
        Id(since[0]).ShouldBe(Id(candidates[0]));
        ChangeSeq(since[0]).ShouldBeGreaterThan(seenUpTo);
        (await GetSessionAsync(editor, sessionId, ChangeSeq(since[0]))).GetProperty("candidates").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task The_change_counter_only_goes_up_for_every_insert_and_update_of_a_session()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(7321);
        await CatalogAsync("Game one", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, code))[0];
        var counters = new List<int> { ChangeSeq(candidate) };

        foreach (var copies in new[] { 2, 3, 4 })
        {
            var updated = await DecideAsync(editor, sessionId, Id(candidate), new { copies });
            counters.Add(ChangeSeq(await ReadJsonAsync(updated)));
        }

        counters.ShouldBe(counters.Order().Distinct().ToList(), "strictly increasing");
        counters.Count.ShouldBe(4);
    }
}
