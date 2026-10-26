using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

// LOCKED acceptance tests of #345: an unreadable spine is counted and findable, never dropped or invented. Do not edit or weaken.
public class ProvisionalLockedTests : ProvisionalTest
{
    public ProvisionalLockedTests(ApiFixture api)
        : base(api)
    {
    }

    [Fact]
    public async Task Provisional_or_counted_no_path_drops_a_kept_segment_without_a_visible_row_or_provisional()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);

        var provisionalId = await KeepIdAsync(editor, sessionId, candidateId);

        // The row is retired but linked, the provisional is open: the segment is visible exactly once, in Needs attention.
        var row = await CandidateAsync(candidateId);
        row.Decision.ShouldBe(CandidateDecision.Rejected);
        row.ProvisionalId.ShouldBe(provisionalId);
        var kept = (await ProvisionalAsync(provisionalId)).ShouldNotBeNull();
        kept.CandidateId.ShouldBe(candidateId);
        kept.ResolvedAt.ShouldBeNull();
        var session = await GetSessionAsync(editor, sessionId);
        var counters = session.GetProperty("counters");
        var captured = counters.GetProperty("captured").GetInt32();
        captured.ShouldBe(
            counters.GetProperty("added").GetInt32()
            + counters.GetProperty("toReview").GetInt32()
            + counters.GetProperty("owned").GetInt32()
            + counters.GetProperty("skipped").GetInt32());
        captured.ShouldBe(2);
        counters.GetProperty("toReview").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_provisional_survives_the_seven_day_session_cleanup_with_its_crop()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var cropId = await CropIdAsync(editor, sessionId, candidateId);
        var provisionalId = await KeepIdAsync(editor, sessionId, candidateId, cropId);
        var now = DateTimeOffset.UtcNow;
        await using (var context = Db.CreateContext())
        {
            await context.ScanSessions.IgnoreQueryFilters().Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now.AddDays(-8)));
        }

        ConfigureServices += services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(now));
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CleanUpScanSessions>().HandleAsync(new CleanUpScanSessions.Job(), CancellationToken.None);
        }

        await using var check = Db.CreateContext();
        (await check.ScanSessions.IgnoreQueryFilters().AnyAsync(s => s.Id == sessionId)).ShouldBeFalse();
        (await check.ScanCandidates.IgnoreQueryFilters().AnyAsync(c => c.Id == candidateId)).ShouldBeFalse();
        var kept = (await ProvisionalAsync(provisionalId)).ShouldNotBeNull();
        kept.ResolvedAt.ShouldBeNull();
        kept.SessionId.ShouldBeNull();
        kept.CandidateId.ShouldBeNull();
        kept.SnapId.ShouldBeNull();
        var asset = (await AssetAsync(cropId)).ShouldNotBeNull();
        StoredFileExists(asset).ShouldBeTrue();
        using var viewer = As(library, LibraryRole.Viewer);
        var list = await viewer.GetAsync(LibraryProvisionalsUrl(library.Id));
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        (await ReadJsonAsync(list)).GetProperty("items").EnumerateArray().Count().ShouldBe(1);
    }

    [Fact]
    public async Task An_uploaded_but_never_kept_crop_is_swept_as_an_orphan()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var cropId = await CropIdAsync(editor, sessionId, candidateId);
        (await AssetAsync(cropId)).ShouldNotBeNull(); // the app was killed before keeping: the crop waits, unclaimed

        ConfigureServices += services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CleanUpScanSessions>().HandleAsync(new CleanUpScanSessions.Job(), CancellationToken.None);
        }

        (await AssetAsync(cropId)).ShouldBeNull();
        FilesOnDisk().ShouldBeEmpty();
        (await CandidateAsync(candidateId)).Decision.ShouldBe(CandidateDecision.Undecided); // the row is still a visible T2 row
    }

    [Fact]
    public async Task Deleting_a_provisional_cascade_deletes_its_crop_bytes_and_row()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var cropId = await CropIdAsync(editor, sessionId, candidateId);
        var provisionalId = await KeepIdAsync(editor, sessionId, candidateId, cropId);

        (await editor.DeleteAsync(ProvisionalUrl(library.Id, provisionalId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ProvisionalAsync(provisionalId)).ShouldBeNull();
        (await AssetAsync(cropId)).ShouldBeNull();
        FilesOnDisk().ShouldBeEmpty();
    }

    [Fact]
    public async Task No_crop_upload_for_auto_resolved_successes_a_committed_candidate_refuses_its_crop()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9301);
        await CatalogAsync("Locked success guard", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id); // crop guard, not auto-add (#341)
        var candidateId = Id((await ScanAsync(editor, sessionId, code))[0]);
        (await DecideAsync(editor, sessionId, candidateId, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await UploadCropAsync(editor, sessionId, candidateId, JpegFixtures.Plain());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        FilesOnDisk().ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_provisional_title_is_marked_a_guess_create_list_session_and_lifecycle()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);

        var created = await ReadJsonAsync(await KeepAsync(editor, sessionId, new { candidateId }));
        created.GetProperty("provisional").GetProperty("isGuess").GetBoolean().ShouldBeTrue();

        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("provisionals").EnumerateArray().ShouldHaveSingleItem().GetProperty("isGuess").GetBoolean().ShouldBeTrue();

        using var viewer = As(library, LibraryRole.Viewer);
        var list = await viewer.GetAsync(LibraryProvisionalsUrl(library.Id));
        (await ReadJsonAsync(list)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem().GetProperty("isGuess").GetBoolean().ShouldBeTrue();
    }

    private static async Task<Guid> CropIdAsync(HttpClient client, Guid sessionId, Guid candidateId)
    {
        var response = await UploadCropAsync(client, sessionId, candidateId, JpegFixtures.Plain());
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("assetId").GetGuid();
    }

    private static async Task<Guid> KeepIdAsync(HttpClient client, Guid sessionId, Guid candidateId, Guid? cropAssetId = null)
    {
        var response = await KeepAsync(client, sessionId, new { candidateId, cropAssetId });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("provisional").GetProperty("id").GetGuid();
    }
}
