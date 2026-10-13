using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Tests.Accounts;

public class GetMeTests
{
    [Fact]
    public void It_returns_the_callers_id_and_every_membership_with_its_role()
    {
        var userId = Guid.CreateVersion7();
        var mine = Guid.CreateVersion7();
        var shared = Guid.CreateVersion7();
        var caller = new CurrentUser();
        caller.Initialize(userId, [new Membership(mine, LibraryRole.Owner), new Membership(shared, LibraryRole.Viewer)]);

        var me = new GetMe.Handler(caller).Handle();

        me.Id.ShouldBe(userId);
        me.Memberships.ShouldBe([new MembershipDto(mine, LibraryRole.Owner), new MembershipDto(shared, LibraryRole.Viewer)]);
    }

    [Fact]
    public void A_caller_who_belongs_to_no_library_has_no_memberships()
    {
        var caller = new CurrentUser();
        caller.Initialize(Guid.CreateVersion7(), []);

        new GetMe.Handler(caller).Handle().Memberships.ShouldBeEmpty();
    }

    [Fact]
    public void Nobody_is_signed_in_so_there_is_nothing_to_return()
    {
        Should.Throw<InvalidOperationException>(() => new GetMe.Handler(new CurrentUser()).Handle());
    }
}
