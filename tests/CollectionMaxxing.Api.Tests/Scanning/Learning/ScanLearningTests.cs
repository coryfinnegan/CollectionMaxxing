using System.Net;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Scanning.Quota;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Scanning.Learning;

public class ScanLearningTests(ApiFixture api) : UpcScanTest(api, dailyLimit: 1000)
{
    private async Task<bool> HasBarcodeAsync(Guid releaseId, string upc)
    {
        Identifier.TryParse(upc, out var id).ShouldBeTrue();
        await using var context = Db.CreateContext();
        return await context.ReleaseIdentifiers.AnyAsync(i => i.ReleaseId == releaseId && i.Value == id!.Value);
    }

    [Fact]
    public async Task A_confirmed_barcode_is_a_local_hit_the_next_time_with_no_provider_request()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6201);
        KnowProduct(code, "Diddy Kong Racing");
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var first = (await ScanAsync(editor, sessionId, code))[0];
        Resolution(first).ShouldBe("matched");
        UpcRequests.ShouldBe(1);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var nextSession = await StartSessionAsync(editor, library.Id);
        var again = (await ScanAsync(editor, nextSession, code))[0];

        Resolution(again).ShouldBe("matched");
        UpcRequests.ShouldBe(1, "the barcode was learned, so no lookup is made");
        again.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(first.GetProperty("release").GetProperty("id").GetGuid());
        (await WasLocalHitAsync(Id(again))).ShouldBeTrue();
        (await WasLocalHitAsync(Id(first))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_rejected_or_never_committed_candidate_teaches_nothing()
    {
        var library = await CreateLibraryAsync();
        var (kept, rejected, abandoned) = (ScanData.Upc(6211), ScanData.Upc(6212), ScanData.Upc(6213));
        KnowProduct(kept, "Sin and Punishment");
        KnowProduct(rejected, "Custom Robo");
        KnowProduct(abandoned, "Mischief Makers");
        using var editor = As(library, LibraryRole.Editor);

        var sessionId = await StartSessionAsync(editor, library.Id);
        var scanned = await ScanAsync(editor, sessionId, kept, rejected);
        (await DecideAsync(editor, sessionId, Id(scanned[1]), new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var neverCommitted = await StartSessionAsync(editor, library.Id);
        var left = (await ScanAsync(editor, neverCommitted, abandoned))[0];
        Decision(left).ShouldBe("accepted");
        (await editor.DeleteAsync(SessionUrl(neverCommitted))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await HasBarcodeAsync(scanned[0].GetProperty("release").GetProperty("id").GetGuid(), kept)).ShouldBeTrue();
        (await HasBarcodeAsync(scanned[1].GetProperty("release").GetProperty("id").GetGuid(), rejected)).ShouldBeFalse();
        (await HasBarcodeAsync(left.GetProperty("release").GetProperty("id").GetGuid(), abandoned)).ShouldBeFalse();

        var next = await StartSessionAsync(editor, library.Id);
        var again = await ScanAsync(editor, next, kept, rejected, abandoned);
        (await WasLocalHitAsync(Id(again[0]))).ShouldBeTrue("the confirmed barcode is a local hit");
        (await WasLocalHitAsync(Id(again[1]))).ShouldBeFalse("the rejected barcode was not learned");
        (await WasLocalHitAsync(Id(again[2]))).ShouldBeFalse("the abandoned one was not learned");
    }

    [Fact]
    public async Task Another_librarys_private_mapping_is_never_used_and_its_own_is()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var code = ScanData.Upc(6221);
        Identifier.TryParse(code, out var id).ShouldBeTrue();
        var (_, privateRelease) = await CatalogSeed.AddAsync(
            Db, "Hand made entry", WorkKind.Game, 1996, barcode: id!.Value, barcodeType: id.Type, ownerLibraryId: theirs.Id);
        using var theirEditor = As(theirs, LibraryRole.Editor);
        using var myEditor = As(mine, LibraryRole.Editor);

        var theirSession = await StartSessionAsync(theirEditor, theirs.Id);
        var theirHit = (await ScanAsync(theirEditor, theirSession, code))[0];
        theirHit.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(privateRelease);

        var mySession = await StartSessionAsync(myEditor, mine.Id);
        var myHit = (await ScanAsync(myEditor, mySession, code))[0];

        Resolution(myHit).ShouldNotBe("matched");
        (myHit.TryGetProperty("release", out var release) && release.ValueKind == System.Text.Json.JsonValueKind.Object
            && release.GetProperty("id").GetGuid() == privateRelease).ShouldBeFalse();
    }

    [Fact]
    public async Task Picking_an_alternative_writes_exactly_one_match_feedback_row()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6231);
        var a = await CatalogAsync("Reused A", code);
        var b = await CatalogAsync("Reused B", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, code))[0];
        Resolution(candidate).ShouldBe("ambiguous");
        var ranked = candidate.GetProperty("alternatives").EnumerateArray().Select(x => x.GetProperty("releaseId").GetGuid()).ToList();
        var pick = ranked[1];

        (await DecideAsync(editor, sessionId, Id(candidate), new { releaseId = pick })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync(editor, sessionId, Id(candidate), new { releaseId = pick })).StatusCode.ShouldBe(HttpStatusCode.OK); // a retry

        var rows = await FeedbackAsync(Id(candidate));
        rows.Count.ShouldBe(1);
        rows[0].RankedFirstReleaseId.ShouldBe(ranked[0]);
        rows[0].PickedReleaseId.ShouldBe(pick);
        new[] { a, b }.ShouldContain(pick);

        // Going back to what we ranked first leaves nothing to learn from.
        (await DecideAsync(editor, sessionId, Id(candidate), new { releaseId = ranked[0] })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FeedbackAsync(Id(candidate))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Picking_what_we_ranked_first_writes_no_feedback()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6241);
        await CatalogAsync("Reused C", code);
        await CatalogAsync("Reused D", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, code))[0];
        var first = candidate.GetProperty("alternatives")[0].GetProperty("releaseId").GetGuid();

        (await DecideAsync(editor, sessionId, Id(candidate), new { releaseId = first })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await FeedbackAsync(Id(candidate))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Sessions_older_than_seven_days_are_removed_and_younger_or_committed_ones_stay()
    {
        // The seeded sessions of other tests are dated 2026-01-01, so the clean-up runs on a date just before that.
        var now = new DateTimeOffset(2025, 12, 20, 0, 0, 0, TimeSpan.Zero);
        var library = await CreateLibraryAsync();
        var oldOpen = await TestData.CreateScanSessionAsync(Db, library);
        var oldAbandoned = await TestData.CreateScanSessionAsync(Db, library);
        var oldCommitted = await TestData.CreateScanSessionAsync(Db, library);
        var young = await TestData.CreateScanSessionAsync(Db, library);
        await using (var context = Db.CreateContext())
        {
            await Age(context, oldOpen, now.AddDays(-8), ScanSessionStatus.Open);
            await Age(context, oldAbandoned, now.AddDays(-9), ScanSessionStatus.Abandoned);
            await Age(context, oldCommitted, now.AddDays(-30), ScanSessionStatus.Committed);
            await Age(context, young, now.AddDays(-6), ScanSessionStatus.Open);
        }

        ConfigureServices += services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(now));
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CleanUpScanSessions>().HandleAsync(new CleanUpScanSessions.Job(), CancellationToken.None);
        }

        await using var check = Db.CreateContext();
        var left = await check.ScanSessions.IgnoreQueryFilters().Where(s => s.LibraryId == library.Id).Select(s => s.Id).ToListAsync();
        left.Order().ToList().ShouldBe(new[] { oldCommitted, young }.Order().ToList());
    }

    private static Task<int> Age(CollectionMaxxing.Infrastructure.Persistence.AppDbContext context, Guid sessionId, DateTimeOffset createdAt, ScanSessionStatus status) =>
        context.ScanSessions.IgnoreQueryFilters().Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, createdAt).SetProperty(x => x.Status, status));

    /// <summary>True when the candidate was answered by the catalogue itself (the first step of its trace), not by a provider.</summary>
    private async Task<bool> WasLocalHitAsync(Guid candidateId)
    {
        await using var context = Db.CreateContext();
        var trace = await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == candidateId).Select(c => c.TraceJson).SingleAsync();
        return trace!.Contains("\"catalogue\"", StringComparison.Ordinal);
    }

    private async Task<List<Domain.Operations.MatchFeedback>> FeedbackAsync(Guid candidateId)
    {
        await using var context = Db.CreateContext();
        return await context.MatchFeedback.IgnoreQueryFilters().Where(f => f.CandidateId == candidateId).ToListAsync();
    }
}
