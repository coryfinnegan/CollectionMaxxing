using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Scanning;

// The owner scans in the evening: 8 pm on 30 October in US Central daylight time (UTC-5) is already 31 October in UTC, and an
// item added then must be dated the 30th, the day the person sees, not tomorrow.
public class ScanLocalDayTests : ScanTest
{
    private const int CentralDaylight = -300;
    private static readonly DateTimeOffset EveningCentral = new(2026, 10, 31, 1, 0, 0, TimeSpan.Zero);

    public ScanLocalDayTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(EveningCentral));
        };
    }

    private static Task<HttpResponseMessage> CommitWithAsync(HttpClient client, Guid sessionId, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/commit") { Content = Json(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    /// <summary>A session with the given create body, one catalogue game scanned into it and accepted.</summary>
    private async Task<(TestLibrary Library, HttpClient Editor, Guid SessionId)> ScannedAsync(int seed, object sessionBody)
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(seed);
        await CatalogAsync($"Evening game {seed}", code);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, sessionBody);
        (await ScanAsync(editor, sessionId, code)).ShouldAllBe(c => Decision(c) == "accepted");
        return (library, editor, sessionId);
    }

    private async Task<DateOnly> OnlyItemDayAsync(Guid libraryId)
    {
        var items = await ScanReaders.ItemsAsync(Db, libraryId);
        return items.ShouldHaveSingleItem().AcquiredOn!.Value;
    }

    [Fact]
    public async Task A_commit_dates_the_items_by_the_phones_day_when_the_session_knows_the_phones_offset()
    {
        var (library, editor, sessionId) = await ScannedAsync(55101, new { autoCommitTiers = Array.Empty<string>(), utcOffsetMinutes = CentralDaylight });
        using var client = editor;

        var commit = await CommitAsync(editor, sessionId);

        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await OnlyItemDayAsync(library.Id)).ShouldBe(new DateOnly(2026, 10, 30));
    }

    [Fact]
    public async Task A_session_that_sent_no_offset_keeps_the_UTC_day()
    {
        var (library, editor, sessionId) = await ScannedAsync(55102, new { autoCommitTiers = Array.Empty<string>() });
        using var client = editor;

        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await OnlyItemDayAsync(library.Id)).ShouldBe(new DateOnly(2026, 10, 31));
    }

    [Fact]
    public async Task A_day_sent_with_the_commit_wins_over_the_sessions_offset()
    {
        var (library, editor, sessionId) = await ScannedAsync(55103, new { autoCommitTiers = Array.Empty<string>(), utcOffsetMinutes = CentralDaylight });
        using var client = editor;

        var commit = await CommitWithAsync(editor, sessionId, new { acquiredOn = "2026-10-29" });

        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await OnlyItemDayAsync(library.Id)).ShouldBe(new DateOnly(2026, 10, 29));
    }

    [Fact]
    public async Task A_day_one_day_ahead_of_UTC_is_accepted_because_the_phone_may_be_east_of_Greenwich()
    {
        var (library, editor, sessionId) = await ScannedAsync(55104, new { autoCommitTiers = Array.Empty<string>() });
        using var client = editor;

        var commit = await CommitWithAsync(editor, sessionId, new { acquiredOn = "2026-11-01" });

        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await OnlyItemDayAsync(library.Id)).ShouldBe(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public async Task A_day_two_days_ahead_is_refused_and_nothing_is_added()
    {
        var (library, editor, sessionId) = await ScannedAsync(55105, new { autoCommitTiers = Array.Empty<string>(), utcOffsetMinutes = CentralDaylight });
        using var client = editor;

        var commit = await CommitWithAsync(editor, sessionId, new { acquiredOn = "2026-11-02" });

        commit.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await commit.Content.ReadAsStringAsync());
        (await ReadJsonAsync(commit)).GetProperty("code").GetString().ShouldBe("scan.acquired_on_invalid");
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK, "the session stayed open for a corrected commit");
        (await OnlyItemDayAsync(library.Id)).ShouldBe(new DateOnly(2026, 10, 30));
    }

    [Fact]
    public async Task Retrying_a_commit_after_midnight_with_the_new_day_returns_the_first_answer_instead_of_a_conflict()
    {
        var (library, editor, sessionId) = await ScannedAsync(55106, new { autoCommitTiers = Array.Empty<string>() });
        using var client = editor;
        var key = Guid.NewGuid().ToString();

        var first = await CommitWithAsync(editor, sessionId, new { acquiredOn = "2026-10-30" }, key);
        var retry = await CommitWithAsync(editor, sessionId, new { acquiredOn = "2026-10-31" }, key);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());
        (await ReadJsonAsync(retry)).GetProperty("itemIds").GetRawText().ShouldBe((await ReadJsonAsync(first)).GetProperty("itemIds").GetRawText());
        (await OnlyItemDayAsync(library.Id)).ShouldBe(new DateOnly(2026, 10, 30));
    }

    [Fact]
    public async Task An_auto_added_item_is_dated_by_the_sessions_offset()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(55107);
        await CatalogAsync("Evening auto-add", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0" }, utcOffsetMinutes = CentralDaylight });

        await ScanAsync(editor, sessionId, code);

        (await OnlyItemDayAsync(library.Id)).ShouldBe(new DateOnly(2026, 10, 30));
    }

    [Fact]
    public async Task An_auto_added_item_in_a_session_with_no_offset_is_dated_the_UTC_day()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(55108);
        await CatalogAsync("Evening auto-add, no offset", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);

        await ScanAsync(editor, sessionId, code);

        (await OnlyItemDayAsync(library.Id)).ShouldBe(new DateOnly(2026, 10, 31));
    }

    [Theory]
    [InlineData(-720)]
    [InlineData(-300)]
    [InlineData(0)]
    [InlineData(840)]
    public async Task A_real_offset_starts_a_session(int minutes)
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsync(SessionsUrl(library.Id), Json(new { utcOffsetMinutes = minutes }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(-721)]
    [InlineData(841)]
    [InlineData(100000)]
    public async Task An_offset_no_time_zone_has_is_refused(int minutes)
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsync(SessionsUrl(library.Id), Json(new { utcOffsetMinutes = minutes }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.utc_offset_invalid");
    }
}
