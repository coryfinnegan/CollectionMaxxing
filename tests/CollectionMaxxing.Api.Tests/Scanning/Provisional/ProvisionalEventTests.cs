using System.Net;
using CollectionMaxxing.Api.Tests.Scanning.Events;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

/// <summary>The session stream announces kept rows (#345): created and resolved records, with counters that follow.</summary>
public class ProvisionalEventTests : ProvisionalTest
{
    public ProvisionalEventTests(ApiFixture api)
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
    public async Task Keeping_a_row_announces_provisional_created_and_moves_the_counters()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        await using var events = await SseClient.ConnectAsync(editor, EventsUrl(sessionId));
        events.StatusCode.ShouldBe(HttpStatusCode.OK);
        await events.ReadUntilAsync("session.counters"); // the baseline, before the keep

        (await KeepAsync(editor, sessionId, new { candidateId })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = (await events.ReadUntilAsync("provisional.created")).Last();
        created.Data!.Value.GetProperty("candidateId").GetGuid().ShouldBe(candidateId);
        created.Data!.Value.GetProperty("isGuess").GetBoolean().ShouldBeTrue();
        var moved = (await events.ReadUntilAsync(
            "session.counters", e => e.Data!.Value.GetProperty("counters").GetProperty("captured").GetInt32() == 2)).Last();
        moved.Data!.Value.GetProperty("counters").GetProperty("toReview").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Promoting_announces_provisional_updated()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var keep = await KeepAsync(editor, sessionId, new { candidateId });
        var provisionalId = (await ReadJsonAsync(keep)).GetProperty("provisional").GetProperty("id").GetGuid();
        await using var events = await SseClient.ConnectAsync(editor, EventsUrl(sessionId));
        events.StatusCode.ShouldBe(HttpStatusCode.OK);
        await events.ReadUntilAsync("session.counters"); // the baseline, before the promote

        var promote = new HttpRequestMessage(HttpMethod.Post, $"{ProvisionalUrl(library.Id, provisionalId)}:promote")
        {
            Content = Json(new { }),
        };
        promote.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await editor.SendAsync(promote)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = (await events.ReadUntilAsync("provisional.updated")).Last();
        updated.Data!.Value.GetProperty("id").GetGuid().ShouldBe(provisionalId);
        updated.Data!.Value.GetProperty("resolveAction").GetString().ShouldBe("promoted");
    }
}
