using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Invites;

// The invites list, from the owner's side. (InviteEndpointTests is LOCKED, so the library filter is pinned here.)
public class InviteListTests(ApiFixture api) : InviteTest(api)
{
    [Fact]
    public async Task An_owner_of_two_libraries_sees_only_the_invites_of_the_library_they_ask_about()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var first = ownerAccount.LibraryId!.Value;
        var second = await owner.CreateLibraryAsync("Second library");
        await CreateInviteAsync(owner, first, "viewer", "Only for the first");
        await CreateInviteAsync(owner, second, "editor", "For the second");
        await CreateInviteAsync(owner, second, "viewer", "Also for the second");

        var inFirst = await NamesAsync(owner, first);
        var inSecond = await NamesAsync(owner, second);

        inFirst.ShouldBe(["Only for the first"]);
        inSecond.Order().ShouldBe(["Also for the second", "For the second"]);
    }

    private static async Task<string[]> NamesAsync(Auth.WebClient owner, Guid libraryId)
    {
        var list = await ReadAsync(await owner.GetAsync(InvitesUrl(libraryId)));
        return [.. list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];
    }
}
