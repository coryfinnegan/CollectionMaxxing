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
}
