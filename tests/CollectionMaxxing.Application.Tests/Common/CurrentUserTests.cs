using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Tests.Common;

public class CurrentUserTests
{
    [Fact]
    public void Before_anyone_signs_in_the_caller_has_no_id_and_no_libraries()
    {
        var user = new CurrentUser();

        user.UserId.ShouldBeNull();
        user.LibraryIds.ShouldBeEmpty();
        user.RoleIn(Guid.CreateVersion7()).ShouldBeNull();
    }

    [Fact]
    public void Roles_are_looked_up_per_library()
    {
        var editorIn = Guid.CreateVersion7();
        var viewerIn = Guid.CreateVersion7();
        var user = new CurrentUser();

        user.Initialize(Guid.CreateVersion7(), [new Membership(editorIn, LibraryRole.Editor), new Membership(viewerIn, LibraryRole.Viewer)]);

        user.RoleIn(editorIn).ShouldBe(LibraryRole.Editor);
        user.RoleIn(viewerIn).ShouldBe(LibraryRole.Viewer);
        user.RoleIn(Guid.CreateVersion7()).ShouldBeNull();
        user.LibraryIds.ShouldBe([editorIn, viewerIn], ignoreOrder: true);
    }

    [Fact]
    public void Memberships_and_library_ids_keep_the_order_they_were_given_in()
    {
        var first = new Membership(Guid.CreateVersion7(), LibraryRole.Viewer);
        var second = new Membership(Guid.CreateVersion7(), LibraryRole.Owner);
        var third = new Membership(Guid.CreateVersion7(), LibraryRole.Editor);
        var userId = Guid.CreateVersion7();
        var user = new CurrentUser();

        user.Initialize(userId, [first, second, third]);

        user.UserId.ShouldBe(userId);
        user.Memberships.ShouldBe([first, second, third]);
        user.LibraryIds.ShouldBe([first.LibraryId, second.LibraryId, third.LibraryId]);
    }

    [Fact]
    public void Two_memberships_in_one_library_are_refused()
    {
        var library = Guid.CreateVersion7();
        var user = new CurrentUser();

        Should.Throw<ArgumentException>(() =>
            user.Initialize(Guid.CreateVersion7(), [new Membership(library, LibraryRole.Viewer), new Membership(library, LibraryRole.Owner)]));
    }

    [Fact]
    public void A_caller_with_no_memberships_belongs_to_no_library()
    {
        var user = new CurrentUser();

        user.Initialize(Guid.CreateVersion7(), []);

        user.Memberships.ShouldBeEmpty();
        user.LibraryIds.ShouldBeEmpty();
        user.RoleIn(Guid.CreateVersion7()).ShouldBeNull();
    }
}
