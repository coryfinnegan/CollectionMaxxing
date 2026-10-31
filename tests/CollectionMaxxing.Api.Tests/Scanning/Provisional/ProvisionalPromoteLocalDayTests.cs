using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

// Promoting a kept row in the evening dates the item the day the person sees (the session's UTC offset), like a scan commit does.
public class ProvisionalPromoteLocalDayTests : ProvisionalTest
{
    private static readonly DateTimeOffset EveningCentral = new(2026, 10, 31, 1, 0, 0, TimeSpan.Zero);

    public ProvisionalPromoteLocalDayTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(EveningCentral));
        };
    }

    private async Task<DateOnly> PromotedDayAsync(object sessionBody)
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, sessionBody);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var kept = await KeepAsync(editor, sessionId, new { candidateId });
        kept.StatusCode.ShouldBe(HttpStatusCode.Created, await kept.Content.ReadAsStringAsync());
        var provisionalId = (await ReadJsonAsync(kept)).GetProperty("provisional").GetProperty("id").GetGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, $"{ProvisionalUrl(library.Id, provisionalId)}:promote") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var promoted = await editor.SendAsync(request);

        promoted.StatusCode.ShouldBe(HttpStatusCode.OK, await promoted.Content.ReadAsStringAsync());
        return (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldHaveSingleItem().AcquiredOn!.Value;
    }

    [Fact]
    public async Task A_promoted_row_is_dated_by_the_offset_its_session_was_started_with()
    {
        (await PromotedDayAsync(new { utcOffsetMinutes = -300 })).ShouldBe(new DateOnly(2026, 10, 30));
    }

    [Fact]
    public async Task A_promoted_row_from_a_session_with_no_offset_is_dated_the_UTC_day()
    {
        (await PromotedDayAsync(new { })).ShouldBe(new DateOnly(2026, 10, 31));
    }
}
