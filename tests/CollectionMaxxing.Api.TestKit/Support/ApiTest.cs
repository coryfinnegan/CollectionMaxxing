using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Xunit;

namespace CollectionMaxxing.Api.Tests.Support;

/// <summary>
/// Base class for tests that call the API: empties the database first and offers small helpers. Every class that derives from it gets its own
/// database and API host (<see cref="ApiFixture"/> is a class fixture), and classes run in parallel.
/// </summary>
public abstract class ApiTest(ApiFixture api) : IAsyncLifetime, IClassFixture<ApiFixture>
{
    protected ApiFixture Api { get; } = api;

    protected PostgresFixture Db => Api.Db;

    public virtual async ValueTask InitializeAsync() => await Db.ResetAsync();

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>A library with an owner, an editor and a viewer.</summary>
    protected Task<TestLibrary> CreateLibraryAsync() => TestData.CreateLibraryAsync(Db);

    protected HttpClient ClientFor(TestLibrary library, LibraryRole role) => Api.CreateClient(library.UserWithRole(role));

    protected static string LocationsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/locations";

    protected static string LocationUrl(Guid libraryId, Guid locationId) => $"{LocationsUrl(libraryId)}/{locationId}";

    protected static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
