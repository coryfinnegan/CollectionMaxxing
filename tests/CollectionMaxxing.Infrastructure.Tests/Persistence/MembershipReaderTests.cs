using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

public class MembershipReaderTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public async Task It_returns_the_callers_memberships_even_though_the_caller_has_no_library_yet()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLibraryAsync(Db); // someone else's

        await using var context = Db.CreateContext();
        IMembershipReader reader = new MembershipReader(context);

        var memberships = await reader.GetMembershipsAsync(library.EditorId, CancellationToken.None);

        memberships.ShouldBe([new Membership(library.Id, LibraryRole.Editor)]);
    }

    [Fact]
    public async Task It_returns_nothing_for_a_user_who_belongs_to_no_library()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        await using var context = Db.CreateContext();
        var memberships = await new MembershipReader(context).GetMembershipsAsync(library.OutsiderId, CancellationToken.None);

        memberships.ShouldBeEmpty();
    }
}
