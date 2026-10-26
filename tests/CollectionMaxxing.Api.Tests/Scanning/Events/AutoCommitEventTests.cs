using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Events;

// Issue #341: a T0 auto-add announces itself as candidate.auto_committed, beside the added or updated of the same change.
public class AutoCommitEventTests : ScanTest
{
    public AutoCommitEventTests(ApiFixture api)
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
    public async Task Inline_auto_add_sends_added_then_updated_with_auto_committed()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34150);
        await CatalogAsync("T0 game", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await using var events = await SseClient.ConnectAsync(editor, EventsUrl(sessionId));

        await ScanAsync(editor, sessionId, code);

        var read = await events.ReadUntilAsync("candidate.auto_committed");
        var committed = read.Last(e => e.Name == "candidate.auto_committed");
        committed.Id.ShouldBeNull("the companion event carries no id, so it never moves the resume cursor");
        committed.Data!.Value.GetProperty("tier").GetString().ShouldBe("T0");
        committed.Data!.Value.GetProperty("createdItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);

        // The poll may have seen the insert and the resolve separately (added, then updated) or together (one added
        // with the resolved row): either way the candidate's latest event shows the add.
        var rows = read.Where(e => e.Name is "candidate.added" or "candidate.updated").ToList();
        rows.ShouldNotBeEmpty();
        rows.Select(e => e.Data!.Value.GetProperty("id").GetGuid())
            .ShouldAllBe(id => id == committed.Data!.Value.GetProperty("id").GetGuid());
        rows.Last().Data!.Value.GetProperty("createdItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
    }

    [Fact]
    public async Task Job_resolved_auto_add_sends_updated_with_auto_committed()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34152);
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

        var candidates = await ScanAsync(editor, sessionId, code);
        Resolution(candidates[0]).ShouldBe("pending");
        (await events.ReadUntilAsync("candidate.added")).ShouldNotBeEmpty();

        slow = false;
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResolvePendingCandidate>().HandleAsync(
                new SubmitBarcodes.ResolveJob(library.Id, sessionId, Id(candidates[0]), library.EditorId), CancellationToken.None);
        }

        var updated = (await events.ReadUntilAsync(
            "candidate.updated", e => e.Data!.Value.GetProperty("resolution").GetString() == "matched")).Last();
        updated.Data!.Value.GetProperty("createdItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
        var committed = await events.ReadUntilAsync("candidate.auto_committed");
        committed.Last().Data!.Value.GetProperty("id").GetGuid().ShouldBe(Id(candidates[0]));
    }

    [Fact]
    public async Task Polling_since_carries_the_auto_add_for_clients_that_missed_the_stream()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34151);
        await CatalogAsync("T0 game", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, sessionId, code);

        var candidates = (await GetSessionAsync(editor, sessionId, 0)).GetProperty("candidates").EnumerateArray().ToList();

        candidates.Count.ShouldBe(1);
        candidates[0].GetProperty("tier").GetString().ShouldBe("T0");
        candidates[0].GetProperty("createdItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
    }
}
