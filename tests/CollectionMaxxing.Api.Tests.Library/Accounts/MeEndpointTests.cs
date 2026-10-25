using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Accounts;

public class MeEndpointTests(ApiFixture api) : ApiTest(api)
{
    [Fact]
    public async Task Me_returns_the_callers_id_and_their_role_in_each_library()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Editor).GetAsync("/api/v1/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await ReadJsonAsync(response);
        me.GetProperty("id").GetGuid().ShouldBe(library.EditorId);
        var membership = me.GetProperty("memberships").EnumerateArray().Single();
        membership.GetProperty("libraryId").GetGuid().ShouldBe(library.Id);
        membership.GetProperty("role").GetString().ShouldBe("editor");
    }

    [Fact]
    public async Task Me_lists_every_library_the_caller_belongs_to_and_no_others()
    {
        var first = await CreateLibraryAsync();
        await CreateLibraryAsync(); // someone else's

        var me = await ReadJsonAsync(await ClientFor(first, LibraryRole.Owner).GetAsync("/api/v1/me"));

        me.GetProperty("memberships").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_signed_in_user_with_no_library_gets_an_empty_list()
    {
        var library = await CreateLibraryAsync();

        var me = await ReadJsonAsync(await Api.CreateClient(library.OutsiderId).GetAsync("/api/v1/me"));

        me.GetProperty("id").GetGuid().ShouldBe(library.OutsiderId);
        me.GetProperty("memberships").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_caller_whose_user_id_is_not_a_guid_is_unauthorized()
    {
        using var client = Api.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "not-a-guid");

        (await client.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
