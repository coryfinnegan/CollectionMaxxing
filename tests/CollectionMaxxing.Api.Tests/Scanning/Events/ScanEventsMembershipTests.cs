using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Events;

/// <summary>
/// ONB-1b (a): a scan stream is open for the person who started the session. If they stop belonging to the library (they left, or the
/// owner removed them) the stream ends at its next look; it does not carry on with the memberships it saw when it opened.
/// </summary>
public class ScanEventsMembershipTests : ScanTest
{
    public ScanEventsMembershipTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services => services.AddSingleton(new ScanEventOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            Heartbeat = TimeSpan.FromMilliseconds(250),
        });
    }

    [Fact]
    public async Task The_stream_ends_when_the_caller_loses_the_library_while_it_is_open()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(5201);
        await CatalogAsync("Kirby 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, code);
        await using var events = await SseClient.ConnectAsync(editor, SessionUrl(sessionId) + "/events");
        (await events.ReadUntilAsync("session.status")).Last().Data!.Value.GetProperty("status").GetString().ShouldBe("open");

        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.UserId == library.EditorId).ExecuteDeleteAsync();
        }

        // Heartbeats and nothing else may arrive before the server closes the stream; it must close, not wait for the client to go away.
        SseEvent? next;
        do
        {
            next = await events.NextAsync(TimeSpan.FromSeconds(10));
        }
        while (next is { IsComment: true });
        next.ShouldBeNull();
    }

    [Fact]
    public async Task A_stream_whose_caller_is_still_a_member_carries_on()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await using var events = await SseClient.ConnectAsync(editor, SessionUrl(sessionId) + "/events");
        await events.ReadUntilAsync("session.status");

        (await events.NextAsync(TimeSpan.FromSeconds(10)))!.IsComment.ShouldBeTrue();
        (await events.NextAsync(TimeSpan.FromSeconds(10)))!.IsComment.ShouldBeTrue();
    }
}
