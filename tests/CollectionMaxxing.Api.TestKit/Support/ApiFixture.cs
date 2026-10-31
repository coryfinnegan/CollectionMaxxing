using CollectionMaxxing.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CollectionMaxxing.Api.Tests.Support;

/// <summary>
/// One database and one API host for one test class: a clone of the assembly's migrated template (see <see cref="PostgresServer"/>), and a
/// <see cref="TestApiFactory"/> pointed at it. Test classes get it through <c>IClassFixture&lt;ApiFixture&gt;</c> (<see cref="ApiTest"/> declares it),
/// so classes run in parallel and never see each other's rows, clock, job queue or caches; the tests inside a class run one after another.
/// To keep a class from running alongside any other, put it in a <c>[CollectionDefinition(..., DisableParallelization = true)]</c> collection.
/// It has no constructor arguments because an assembly fixture cannot take another fixture (xUnit3005) and the Auth assembly also registers
/// one shared instance, for a locked class that names <c>ApiFixture</c> but declares no class fixture.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    /// <summary>This class's own database.</summary>
    public PostgresFixture Db { get; private set; } = null!;

    public TestApiFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Db = await PostgresServer.Shared.CreateDatabaseAsync();
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
        GC.SuppressFinalize(this);
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (Db is not null)
        {
            await Db.DisposeAsync();
        }
    }
}
