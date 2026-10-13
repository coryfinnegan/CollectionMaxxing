using CollectionMaxxing.TestSupport;
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
        var client = Factory.CreateClient();
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
