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
        var before = scan.Candidate.GetProperty("seq").GetInt32();
        await using var events = await SseClient.ConnectAsync(scan.Client, $"{SessionUrl(scan.SessionId)}/events");
        await events.ReadUntilAsync("session.status");

        var response = await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.1));
        (await ReadOkAsync(response)).GetProperty("seq").GetInt32().ShouldBeGreaterThan(before);

        var updated = (await events.ReadUntilAsync("candidate.updated")).Last();
        updated.Seq.ShouldBeGreaterThan(before);
        updated.Data!.Value.GetProperty("id").GetGuid().ShouldBe(scan.CandidateId);
        updated.Data!.Value.GetProperty("resolution").GetString().ShouldBe("matched");
        updated.Data!.Value.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(ids[1]);
    }

    [Fact]
    public async Task A_client_that_reconnects_after_the_update_is_sent_the_changed_candidate()
    {
        var scan = await ScanOriginalAndPlayersChoiceAsync();
        var ids = AlternativeIds(scan.Candidate);
        var lastSeen = scan.Candidate.GetProperty("seq").GetInt32();
        (await ReadOkAsync(await PostArtAsync(scan.Client, scan, Distance(ids[0], 1.3), Distance(ids[1], 0.1)))).GetProperty("seq").GetInt32()
            .ShouldBeGreaterThan(lastSeen);

        await using var events = await SseClient.ConnectAsync(scan.Client, $"{SessionUrl(scan.SessionId)}/events", lastSeen.ToString());
        var replayed = await events.ReadUntilAsync("session.status");

        var candidate = replayed.Single(e => e.Name.StartsWith("candidate.", StringComparison.Ordinal));
        candidate.Data!.Value.GetProperty("id").GetGuid().ShouldBe(scan.CandidateId);
        candidate.Data!.Value.GetProperty("resolution").GetString().ShouldBe("matched");
    }
}
