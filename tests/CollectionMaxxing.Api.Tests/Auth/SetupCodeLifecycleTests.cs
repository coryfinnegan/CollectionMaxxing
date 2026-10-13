using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Tests.Auth;

public class SetupCodeLifecycleTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public async Task The_announcer_writes_the_code_to_secrets_setup_code_txt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cm-announce-" + Guid.NewGuid().ToString("N"));
        try
        {
            var announcer = new SetupCodeAnnouncer(Options.Create(new AuthOptions { SecretsPath = directory }), NullLogger<SetupCodeAnnouncer>.Instance);

            await announcer.AnnounceAsync("ABCD-EFGH-JKLM-NPQR", DateTimeOffset.Parse("2026-10-13T12:00:00Z"), CancellationToken.None);

            var written = await File.ReadAllTextAsync(Path.Combine(directory, "setup-code.txt"));
            written.ShouldContain("ABCD-EFGH-JKLM-NPQR");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_new_server_with_no_owner_issues_a_setup_code_on_start_and_announces_it()
    {
        var announced = new TaskCompletionSource<(string Code, DateTimeOffset Expires)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new AuthApp(
            Db.ConnectionString,
            new() { ["Auth:Setup:IssueOnStartup"] = "true" },
            services =>
            {
                services.RemoveAll<ISetupCodeAnnouncer>();
                services.AddSingleton<ISetupCodeAnnouncer>(new CapturingAnnouncer(announced));
            });
        _ = app.Services; // start the host
        using var client = app.NewBrowser();
        await client.GetAsync("/setup"); // make sure the host is up

        var (code, expires) = await announced.Task.WaitAsync(TimeSpan.FromSeconds(30));

        expires.ShouldBe(app.Time.GetUtcNow() + TimeSpan.FromHours(24));
        var owner = await app.NewBrowser().SetUpOwnerAsync(code);
        owner.LibraryId.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_new_server_that_already_has_an_owner_issues_no_setup_code()
    {
        await SetUpOwnerAsync();
        var announced = new TaskCompletionSource<(string Code, DateTimeOffset Expires)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = new AuthApp(
            Db.ConnectionString,
            new() { ["Auth:Setup:IssueOnStartup"] = "true" },
            services =>
            {
                services.RemoveAll<ISetupCodeAnnouncer>();
                services.AddSingleton<ISetupCodeAnnouncer>(new CapturingAnnouncer(announced));
            });
        using var client = app.NewBrowser();
        await client.GetAsync("/account/login");

        await Task.Delay(TimeSpan.FromSeconds(2));

        announced.Task.IsCompleted.ShouldBeFalse();
    }

    private sealed class CapturingAnnouncer(TaskCompletionSource<(string Code, DateTimeOffset Expires)> announced) : ISetupCodeAnnouncer
    {
        public Task AnnounceAsync(string code, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            announced.TrySetResult((code, expiresAt));
            return Task.CompletedTask;
        }
    }
}
