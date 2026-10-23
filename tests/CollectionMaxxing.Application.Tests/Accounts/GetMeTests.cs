using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Tests.Accounts;

public class GetMeTests
{
    private readonly FakeUsers _users = new();

    [Fact]
    public async Task It_returns_the_callers_id_and_every_membership_with_its_role()
    {
        var userId = Guid.CreateVersion7();
        var mine = Guid.CreateVersion7();
        var shared = Guid.CreateVersion7();
        var caller = new CurrentUser();
        caller.Initialize(userId, [new Membership(mine, LibraryRole.Owner), new Membership(shared, LibraryRole.Viewer)]);

        var me = await new GetMe.Handler(caller, _users).HandleAsync(CancellationToken.None);

        me.Id.ShouldBe(userId);
        me.Memberships.ShouldBe([new MembershipDto(mine, LibraryRole.Owner), new MembershipDto(shared, LibraryRole.Viewer)]);
    }

    [Fact]
    public async Task It_returns_the_accounts_display_name_and_email()
    {
        var userId = Guid.CreateVersion7();
        var caller = new CurrentUser();
        caller.Initialize(userId, []);
        _users.Profiles[userId] = new AccountProfile("Ana", "ana@example.com");

        var me = await new GetMe.Handler(caller, _users).HandleAsync(CancellationToken.None);

        (me.DisplayName, me.Email).ShouldBe(("Ana", "ana@example.com"));
    }

    [Fact]
    public async Task A_caller_who_belongs_to_no_library_has_no_memberships()
    {
        var caller = new CurrentUser();
        caller.Initialize(Guid.CreateVersion7(), []);

        (await new GetMe.Handler(caller, _users).HandleAsync(CancellationToken.None)).Memberships.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nobody_is_signed_in_so_there_is_nothing_to_return()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => new GetMe.Handler(new CurrentUser(), _users).HandleAsync(CancellationToken.None));
    }

    private sealed class FakeUsers : IUserDirectory
    {
        public Dictionary<Guid, AccountProfile> Profiles { get; } = [];

        public Task<AccountProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Profiles.GetValueOrDefault(userId));

        public Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<bool> IsDeactivatedAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<Guid>> FindActiveByEmailAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}
