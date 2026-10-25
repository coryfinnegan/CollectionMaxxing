using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

/// <summary>
/// Issue #282, the <c>/identify</c> twin of <see cref="Scanning.ResolveJobMembershipTests"/>: the job re-reads who the
/// scanner belongs to now, and someone removed from the library since gets nothing written into it.
/// </summary>
public class IdentifyResolveMembershipTests(ApiFixture api) : IdentifyTest(api)
{
    [Fact]
    public async Task A_removed_members_pending_item_is_not_resolved_by_the_job()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var slow = true;
        Games.Results.Add(ScanData.Game("Zorblax Quest V", "Nintendo 64", 1999, "g-zorblax-v"));
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var pending = (await IdentifyOkAsync(editor, sessionId, Read(title: "Zorblax Quest V", kind: "game")))[0];
        Resolution(pending).ShouldBe("pending");

        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.UserId == library.EditorId).ExecuteDeleteAsync();
        }

        slow = false;
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResolvePendingIdentify>().HandleAsync(
                new IdentifyItems.ResolveJob(library.Id, sessionId, Id(pending), library.EditorId), CancellationToken.None);
        }

        await using (var context = Db.CreateContext())
        {
            (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(pending))).Resolution
                .ShouldBe(CandidateResolution.Pending);
        }

        Logs.Lines.ShouldContain(line => line.Contains("no longer belongs to the library", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_current_members_pending_item_is_still_resolved_by_the_job()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var slow = true;
        Games.Results.Add(ScanData.Game("Zorblax Quest VI", "Nintendo 64", 2000, "g-zorblax-vi"));
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var pending = (await IdentifyOkAsync(editor, sessionId, Read(title: "Zorblax Quest VI", kind: "game")))[0];
        Resolution(pending).ShouldBe("pending");

        slow = false;
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ResolvePendingIdentify>().HandleAsync(
                new IdentifyItems.ResolveJob(library.Id, sessionId, Id(pending), library.EditorId), CancellationToken.None);
        }

        await using (var context = Db.CreateContext())
        {
            (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(pending))).Resolution
                .ShouldBe(CandidateResolution.Matched);
        }
    }
}
