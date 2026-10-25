using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Tests.Scanning;

public class ReleaseStoreTests(PostgresFixture db) : DatabaseTest(db)
{
    private static ProviderRelease Book(string? coverUrl, string isbn = "9780441172719") => new()
    {
        Provider = "openlibrary",
        ExternalId = "OL1M",
        Title = "Dune",
        Kind = WorkKind.Book,
        Format = ReleaseFormat.Paperback,
        Year = 1965,
        CoverUrl = coverUrl,
        Identifiers = [Identifier.TryParse(isbn, out var id) ? id! : throw new InvalidOperationException()],
    };

    private static string? CoverUrlOf(string? payloadJson)
    {
        if (payloadJson is null)
        {
            return null;
        }

        using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty("coverUrl", out var url) && url.ValueKind == System.Text.Json.JsonValueKind.String
            ? url.GetString()
            : null;
    }

    [Fact]
    public async Task A_new_release_keeps_the_cover_url_its_provider_named()
    {
        await using var context = Db.CreateContext();
        var store = new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System);

        var ensured = await store.EnsureAsync(Book("https://covers.openlibrary.org/b/id/42-L.jpg"), TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        ensured.IsNew.ShouldBeTrue();
        CoverUrlOf((await context.Releases.AsNoTracking().SingleAsync(r => r.Id == ensured.Id)).ProviderPayloadsJson)
            .ShouldBe("https://covers.openlibrary.org/b/id/42-L.jpg");
    }

    [Fact]
    public async Task Reusing_a_release_fills_a_missing_cover_url_from_the_fresh_answer()
    {
        await using var context = Db.CreateContext();
        var store = new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System);
        var first = await store.EnsureAsync(Book(null), TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        var again = await store.EnsureAsync(
            Book("https://covers.openlibrary.org/b/id/42-L.jpg"), TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        again.ShouldBe(new EnsuredRelease(first.Id, false));
        CoverUrlOf((await context.Releases.AsNoTracking().SingleAsync(r => r.Id == first.Id)).ProviderPayloadsJson)
            .ShouldBe("https://covers.openlibrary.org/b/id/42-L.jpg");
    }

    [Fact]
    public async Task Reusing_a_release_keeps_the_cover_url_it_already_has()
    {
        await using var context = Db.CreateContext();
        var store = new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System);
        var first = await store.EnsureAsync(
            Book("https://covers.openlibrary.org/b/id/42-L.jpg"), TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        var again = await store.EnsureAsync(
            Book("https://books.google.com/other.jpg"), TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        again.ShouldBe(new EnsuredRelease(first.Id, false));
        CoverUrlOf((await context.Releases.AsNoTracking().SingleAsync(r => r.Id == first.Id)).ProviderPayloadsJson)
            .ShouldBe("https://covers.openlibrary.org/b/id/42-L.jpg");
    }
}
