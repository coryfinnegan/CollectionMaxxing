using CollectionMaxxing.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CollectionMaxxing.Api.Tests.Support;

/// <summary>One Postgres container and one running API for the whole assembly.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public PostgresFixture Db { get; } = new();

    public TestApiFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Db.InitializeAsync();
        Factory = new TestApiFactory(Db.ConnectionString);
    }

    /// <summary>A client that signs in as <paramref name="userId"/> (with platform <paramref name="roles"/>, e.g. admin), or stays anonymous when null.</summary>
    public HttpClient CreateClient(Guid? userId = null, string? roles = null)
    {
        // The phone's downloader cannot refuse a redirect, so tests look at the first answer rather than the one a redirect leads to.
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, id.ToString());
        }

        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }
}
