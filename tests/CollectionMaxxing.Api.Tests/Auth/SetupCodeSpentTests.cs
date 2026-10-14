using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>Follow-ups from the ID-1 verifier (issue #21) about the setup code once it is spent.</summary>
public class SetupCodeSpentTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public async Task A_spent_setup_code_is_refused_as_used_even_though_no_owner_exists_yet()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        await using (var context = Db.CreateContext())
        {
            await context.SetupCodes.ExecuteUpdateAsync(set => set.SetProperty(c => c.UsedAt, App.Time.GetUtcNow()));
        }

        var response = await App.NewBrowser().StartSetupAsync(code);

        // 410 "used", not the 404 an existing owner would give: the refusal comes from the code itself.
        await ShouldBeProblemAsync(response, HttpStatusCode.Gone);
        (await ReadAsync(response)).GetProperty("code").GetString().ShouldBe("code.used");
        (await CountOwnersAsync()).ShouldBe(0);
        (await CountUsersAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task The_setup_code_file_is_deleted_once_the_owner_exists()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        await App.Services.GetRequiredService<ISetupCodeAnnouncer>().AnnounceAsync(code, App.Time.GetUtcNow().AddHours(24), CancellationToken.None);
        var file = Path.Combine(App.SecretsPath, SetupCodeAnnouncer.FileName);
        File.Exists(file).ShouldBeTrue("the announcer writes the file for the person at the PC");

        await App.NewBrowser().SetUpOwnerAsync(code);

        File.Exists(file).ShouldBeFalse("the code is spent, so the file is just clutter");
    }

    [Fact]
    public async Task The_setup_code_file_stays_while_setup_has_not_finished()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        await App.Services.GetRequiredService<ISetupCodeAnnouncer>().AnnounceAsync(code, App.Time.GetUtcNow().AddHours(24), CancellationToken.None);
        var file = Path.Combine(App.SecretsPath, SetupCodeAnnouncer.FileName);

        (await App.NewBrowser().StartSetupAsync("AAAA-AAAA-AAAA-AAAA")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.NewBrowser().StartSetupAsync(code)).StatusCode.ShouldBe(HttpStatusCode.OK);

        File.Exists(file).ShouldBeTrue();
    }
}
