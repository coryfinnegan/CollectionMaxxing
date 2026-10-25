using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning;

/// <summary>
/// Issue #282: a background resolve job runs from a payload that may be days old, so it re-reads who the scanner
/// belongs to now. Someone removed from the library since gets nothing written into it.
/// </summary>
public class ResolveJobMembershipTests : ScanTest
{
    public ResolveJobMembershipTests(ApiFixture api)
        : base(api)
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
    }

    [Fact]
    public async Task A_removed_members_pending_code_is_not_resolved_by_the_job()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(5301);
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
        var candidates = await ScanAsync(editor, sessionId, code);
        Resolution(candidates[0]).ShouldBe("pending");

        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.UserId == library.EditorId).ExecuteDeleteAsync();
        }

        slow = false;
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResolvePendingCandidate>().HandleAsync(
                new SubmitBarcodes.ResolveJob(library.Id, sessionId, Id(candidates[0]), library.EditorId), CancellationToken.None);
        }

        await using (var context = Db.CreateContext())
        {
            (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(candidates[0]))).Resolution
                .ShouldBe(CandidateResolution.Pending);
        }

        Logs.Lines.ShouldContain(line => line.Contains("no longer belongs to the library", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_current_members_pending_code_is_still_resolved_by_the_job()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(5302);
        var slow = true;
        Products.Behaviour = async (_, ct) =>
        {
            if (slow)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }

            return ProductLookupResult.Found(ScanData.Product("1080 Snowboarding", code));
        };
        Games.Results.Add(ScanData.Game("1080 Snowboarding"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, code);
        Resolution(candidates[0]).ShouldBe("pending");

        slow = false;
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResolvePendingCandidate>().HandleAsync(
                new SubmitBarcodes.ResolveJob(library.Id, sessionId, Id(candidates[0]), library.EditorId), CancellationToken.None);
        }

        await using (var context = Db.CreateContext())
        {
            (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(candidates[0]))).Resolution
                .ShouldBe(CandidateResolution.Matched);
        }
    }
}
