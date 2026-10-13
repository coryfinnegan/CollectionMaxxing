using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance test of ID-1 (issue #14): removing the last passkey is blocked. Do not edit or weaken: add new tests instead.
public class LastPasskeyTests(ApiFixture api) : AuthTest(api)
{
    private async Task<Result> RemoveAsync(Guid userId, string credentialId)
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RemovePasskey.Handler>()
            .HandleAsync(new RemovePasskey.Command(userId, credentialId), CancellationToken.None);
    }

    [Fact]
    public async Task Removing_the_last_passkey_is_blocked()
    {
        var (browser, owner) = await SetUpOwnerAsync();

        var result = await RemoveAsync(owner.UserId, browser.Authenticator.CredentialIdText);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Code.ShouldBe("passkey.last");
        await using var context = Db.CreateContext();
        (await context.UserPasskeys.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task With_two_passkeys_one_can_go_but_never_the_last()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var first = browser.Authenticator;
        var options = await browser.PostAsync("/account/passkey/creation-options", new { displayName = "Phone" });
        browser.Authenticator = new SoftwareAuthenticator();
        (await browser.RegisterAsync(options)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var second = browser.Authenticator;

        (await RemoveAsync(owner.UserId, first.CredentialIdText)).IsSuccess.ShouldBeTrue();
        var last = await RemoveAsync(owner.UserId, second.CredentialIdText);

        last.IsSuccess.ShouldBeFalse();
        last.Error!.Code.ShouldBe("passkey.last");
        await using var context = Db.CreateContext();
        (await context.UserPasskeys.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Removing_a_passkey_that_is_not_theirs_is_not_found()
    {
        var (_, owner) = await SetUpOwnerAsync();

        var result = await RemoveAsync(owner.UserId, "AAAAAAAA");

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}
