using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

using IAppDbContext = CollectionMaxxing.Application.Common.IAppDbContext;
using IDeviceAuthorizations = CollectionMaxxing.Application.Accounts.Devices.IDeviceAuthorizations;

namespace CollectionMaxxing.Api.Tests.Deletion;

// LIB-2d fix round (PR #106): a delete that fails halfway leaves the person active so they can try again.
public class DeleteRetryTests(ApiFixture api) : DeletionTest(api)
{
    [Fact]
    public async Task A_delete_that_fails_halfway_leaves_the_person_active_and_a_retry_finishes_it()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (sam, samAccount) = await AddMemberAsync(owner, ownerAccount.LibraryId!.Value, "editor", "Sam");
        await using var scope = App.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var failing = Substitute.For<IDeviceAuthorizations>();
        failing.RevokeAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("The database went away."));

        await Should.ThrowAsync<IOException>(() => HandlerWith(services, failing).HandleAsync(samAccount.UserId, CancellationToken.None));

        (await UserRowAsync(samAccount.UserId)).DeletedAt.ShouldBeNull("nothing was half done: the person is still active");
        (await sam.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var context = Db.CreateContext())
        {
            (await context.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.UserId == samAccount.UserId)).ShouldBe(1, "still a member");
            (await context.AuditEvents.CountAsync(e => e.Action == AccountAuditActions.Deleted && e.EntityId == samAccount.UserId)).ShouldBe(0);
        }

        var retried = await HandlerWith(services, services.GetRequiredService<IDeviceAuthorizations>())
            .HandleAsync(samAccount.UserId, CancellationToken.None);

        retried.IsSuccess.ShouldBeTrue();
        (await UserRowAsync(samAccount.UserId)).DeletedAt.ShouldNotBeNull();
        await using var after = Db.CreateContext();
        (await after.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.UserId == samAccount.UserId)).ShouldBe(0);
        (await after.AuditEvents.CountAsync(e => e.Action == AccountAuditActions.Deleted && e.EntityId == samAccount.UserId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_purged_person_who_still_has_an_export_record_is_found_by_the_sweeps_backlog_until_it_goes()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (sam, samAccount) = await AddMemberAsync(owner, ownerAccount.LibraryId!.Value, "editor", "Sam");
        (await sam.GetAsync("/api/v1/me/export")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await DeleteAccountAsync(sam)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = App.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<IDeletedAccounts>().EraseAsync(samAccount.UserId, CancellationToken.None);
        var backlog = services.GetRequiredService<IPurgeBacklog>();
        var cutoff = App.Time.GetUtcNow().AddDays(1);

        (await backlog.UsersAwaitingPurgeAsync(cutoff, CancellationToken.None)).ShouldContain(samAccount.UserId, "the export record is left to remove");

        await services.GetRequiredService<CollectionMaxxing.Application.Exports.IExportJobs>().DeleteForUserAsync(samAccount.UserId, CancellationToken.None);

        (await backlog.UsersAwaitingPurgeAsync(cutoff, CancellationToken.None)).ShouldNotContain(samAccount.UserId);
    }
    private static DeleteAccount.Handler HandlerWith(IServiceProvider services, IDeviceAuthorizations devices) =>
        new(
            services.GetRequiredService<IAppDbContext>(),
            services.GetRequiredService<IAccountDeactivation>(),
            devices,
            services.GetRequiredService<IJobQueue>(),
            services.GetRequiredService<IAuditLog>(),
            services.GetRequiredService<TimeProvider>());
}
