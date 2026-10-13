using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;
using AdminCommands = global::CollectionMaxxing.Api.Auth.AdminCommands;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>OpenIddict is set up completely in ID-1; the grants themselves are ID-2.</summary>
public class TokenServerTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public async Task A_well_formed_pairing_grant_reaches_the_token_endpoint_which_is_a_stub_until_ID_2()
    {
        using var browser = App.NewBrowser();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:cm:device_pairing",
            ["pairing_token"] = "x",
        });

        using var response = await browser.Http.PostAsync("/connect/token", form);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task A_grant_nobody_allows_is_refused_by_the_server()
    {
        using var browser = App.NewBrowser();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = "a", ["password"] = "b" });

        using var response = await browser.Http.PostAsync("/connect/token", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_development_signing_and_encryption_keys_are_generated_in_the_secrets_folder_not_the_repository()
    {
        using var browser = App.NewBrowser();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "urn:cm:device_pairing" });
        (await browser.Http.PostAsync("/connect/token", form)).StatusCode.ShouldBe(HttpStatusCode.NotImplemented);

        var secrets = new DirectoryInfo(App.SecretsPath);
        secrets.GetFiles("*.pfx").Select(f => f.Name).Order().ShouldBe(["openiddict-encryption.pfx", "openiddict-signing.pfx"]);
    }

    [Fact]
    public async Task The_recovery_command_prints_a_code_that_recovers_the_owner()
    {
        var (_, owner) = await SetUpOwnerAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await AdminCommands.RunRecoveryCodeAsync(App.Services, output, error, CancellationToken.None);

        exit.ShouldBe(0);
        var code = output.ToString().Split("Recovery code: ")[1].Split(Environment.NewLine)[0].Trim();
        var device = App.NewBrowser();
        var registered = await device.RegisterAsync(await device.StartRecoveryAsync(code));
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        owner.UserId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task The_recovery_command_refuses_when_there_is_no_owner_yet()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await AdminCommands.RunRecoveryCodeAsync(App.Services, output, error, CancellationToken.None);

        exit.ShouldBe(1);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("no owner");
        (await App.IssueCodeAsync(SetupCodePurpose.Setup)).ShouldNotBeNullOrEmpty();
    }
}
