using System.Net.Http.Headers;
using System.Text;
using CollectionMaxxing.Api.Tests.Deletion;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>
/// Base class for the AUTH-2 tests: real sign-in against the fake Auth0 tenant, plus a fake Management API (<see cref="Management"/>) that the
/// server is configured to use, so a test can see exactly which calls Auth0 would have received and make them fail.
/// </summary>
public abstract class ManagementTest(ApiFixture api) : DeletionTest(api)
{
    protected const string AppSignIns = "/api/v1/me/app-sign-ins";

    protected FakeManagementApi Management { get; } = new();

    protected override Dictionary<string, string?>? MoreSettings
    {
        get
        {
            var settings = base.MoreSettings ?? [];
            foreach (var (key, value) in FakeManagementApi.Settings)
            {
                settings[key] = value;
            }

            return settings;
        }
    }

    protected override void ConfigureServices(IServiceCollection services) => Management.Configure(services);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Management.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>An Auth0 access token for <paramref name="subject"/> that was issued at <paramref name="issuedAt"/> (whole seconds, as Auth0 writes <c>iat</c>).</summary>
    protected string TokenIssuedAt(string subject, DateTimeOffset issuedAt) =>
        App.Auth0.AccessToken(subject, claims => claims["iat"] = issuedAt.ToUnixTimeSeconds());

    /// <summary>A token the phone got a little while before now: valid, and older than anything that happens in the test.</summary>
    protected string EarlierToken(string subject) => TokenIssuedAt(subject, App.Time.GetUtcNow() - TimeSpan.FromSeconds(30));

    protected Task<HttpResponseMessage> CallAsync(HttpMethod method, string url, string accessToken, string? body = null, Guid? installId = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (installId is { } install)
        {
            request.Headers.Add("X-Install-Id", install.ToString());
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return App.NewBrowser().Http.SendAsync(request);
    }

    protected Task<HttpResponseMessage> RegisterAsync(string accessToken, Guid installId, string deviceName = "Cory's iPhone") =>
        CallAsync(HttpMethod.Post, AppSignIns, accessToken, $$"""{ "deviceName": "{{deviceName}}" }""", installId);

    protected async Task<List<Job>> JobsAsync(string type)
    {
        await using var context = Db.CreateContext();
        return await context.Jobs.AsNoTracking().Where(j => j.Type == type).ToListAsync();
    }

    protected async Task<DateTimeOffset?> TokensNotBeforeAsync(Guid userId)
    {
        await using var context = Db.CreateContext();
        return await context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TokensNotBefore).SingleAsync();
    }

    protected async Task<int> CountAppSignInsAsync(Guid userId)
    {
        await using var context = Db.CreateContext();
        return await context.AppSignIns.CountAsync(s => s.UserId == userId);
    }
}
