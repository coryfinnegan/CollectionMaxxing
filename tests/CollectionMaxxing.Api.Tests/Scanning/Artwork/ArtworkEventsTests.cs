using CollectionMaxxing.Api.Tests.Scanning.Events;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Events;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Artwork;

public class ArtworkEventsTests : ArtworkTest
{
    public ArtworkEventsTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services => services.AddSingleton(new ScanEventOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            Heartbeat = TimeSpan.FromMilliseconds(250),
        });
    }

    [Fact]
    public async Task Scoring_artwork_emits_candidate_updated_with_a_higher_seq()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var before = scan.Candidate.GetProperty("changeSeq").GetInt32();
        await using var events = await SseClient.ConnectAsync(scan.Client, $"{SessionUrl(scan.SessionId)}/events");
        await events.ReadUntilAsync("session.status");

        var response = await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.1));
        (await ReadOkAsync(response)).GetProperty("changeSeq").GetInt32().ShouldBeGreaterThan(before);

        var updated = (await events.ReadUntilAsync("candidate.updated")).Last();
        updated.Data!.Value.GetProperty("changeSeq").GetInt32().ShouldBeGreaterThan(before);
        updated.Id.ShouldBe(updated.Data!.Value.GetProperty("changeSeq").GetInt32().ToString());
        updated.Data!.Value.GetProperty("id").GetGuid().ShouldBe(scan.CandidateId);
        updated.Data!.Value.GetProperty("resolution").GetString().ShouldBe("matched");
        updated.Data!.Value.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(ids[1]);
    }

    [Fact]
    public async Task A_client_that_reconnects_after_the_update_is_sent_the_changed_candidate()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var lastSeen = scan.Candidate.GetProperty("changeSeq").GetInt32();
        (await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.1)))).GetProperty("changeSeq").GetInt32()
            .ShouldBeGreaterThan(lastSeen);

        await using var events = await SseClient.ConnectAsync(scan.Client, $"{SessionUrl(scan.SessionId)}/events", lastSeen.ToString());
        var replayed = await events.ReadUntilAsync("session.status");

        var candidate = replayed.Single(e => e.Name.StartsWith("candidate.", StringComparison.Ordinal));
        candidate.Data!.Value.GetProperty("id").GetGuid().ShouldBe(scan.CandidateId);
        candidate.Data!.Value.GetProperty("resolution").GetString().ShouldBe("matched");
    }

    [Fact]
    public async Task A_rescored_candidate_is_replayed_after_a_reconnect_as_updated_and_an_unchanged_one_is_not()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var other = (await IdentifyOkAsync(scan.Client, scan.SessionId, Read(title: "Zorblax Quest IV", kind: "game")))[0];

        // The client has seen both candidates; the newest change it saw is the second one's.
        var lastSeen = other.GetProperty("changeSeq").GetInt32();
        var rescored = await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.1)));
        rescored.GetProperty("changeSeq").GetInt32().ShouldBeGreaterThan(lastSeen);

        await using var events = await SseClient.ConnectAsync(scan.Client, $"{SessionUrl(scan.SessionId)}/events", lastSeen.ToString());
        var replayed = await events.ReadUntilAsync("session.status");

        var candidate = replayed.Single(e => e.Name.StartsWith("candidate.", StringComparison.Ordinal));
        candidate.Name.ShouldBe("candidate.updated"); // the client already knew it: it is not "added" again
        candidate.Data!.Value.GetProperty("id").GetGuid().ShouldBe(scan.CandidateId);
        candidate.Data!.Value.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(ids[1]);
        candidate.Id.ShouldBe(rescored.GetProperty("changeSeq").GetInt32().ToString());
    }

    [Fact]
    public async Task The_poll_fallback_after_a_rescore_returns_only_the_rescored_candidate()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var other = (await IdentifyOkAsync(scan.Client, scan.SessionId, Read(title: "Zorblax Quest IV", kind: "game")))[0];
        var polledUpTo = other.GetProperty("changeSeq").GetInt32();

        await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.1)));

        var since = (await GetSessionAsync(scan.Client, scan.SessionId, since: polledUpTo)).GetProperty("candidates").EnumerateArray().ToList();
        since.Count.ShouldBe(1);
        since[0].GetProperty("id").GetGuid().ShouldBe(scan.CandidateId);
    }
}
