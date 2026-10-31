using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Tests.Scanning;

public class ReleaseStoreTests(PostgresFixture db) : DatabaseTest(db)
{
    /// <summary>The library the provider answers are for; none of these tests teach it a barcode, so it needs no row.</summary>
    private static readonly Guid Library = Guid.CreateVersion7();

    private static ProviderRelease Book(string? coverUrl, string isbn = "9780441172719", ReleaseFormat? format = ReleaseFormat.Paperback) => new()
    {
        Provider = "openlibrary",
        ExternalId = "OL1M",
        Title = "Dune",
        Kind = WorkKind.Book,
        Format = format,
        Year = 1965,
        CoverUrl = coverUrl,
        Identifiers = [Identifier.TryParse(isbn, out var id) ? id! : throw new InvalidOperationException()],
    };

    private static ProviderRelease Novel(string isbn, string externalId, params string[] creators) => new()
    {
        Provider = "openlibrary",
        ExternalId = externalId,
        Title = "Suddenly You",
        Kind = WorkKind.Book,
        Format = ReleaseFormat.Paperback,
        Year = 2012,
        Creators = creators,
        Identifiers = [Identifier.TryParse(isbn, out var id) ? id! : throw new InvalidOperationException()],
    };

    private static IReadOnlyList<string> CreatorsOf(string? json)
    {
        if (json is null)
        {
            return [];
        }

        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array
            ? []
            : [.. document.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(e => e.GetString()!)];
    }

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

        var ensured = await store.EnsureAsync(Book("https://covers.openlibrary.org/b/id/42-L.jpg"), Library, TestContext.Current.CancellationToken);
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
        var first = await store.EnsureAsync(Book(null), Library, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        var again = await store.EnsureAsync(
            Book("https://covers.openlibrary.org/b/id/42-L.jpg"), Library, TestContext.Current.CancellationToken);
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
            Book("https://covers.openlibrary.org/b/id/42-L.jpg"), Library, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        var again = await store.EnsureAsync(
            Book("https://books.google.com/other.jpg"), Library, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        again.ShouldBe(new EnsuredRelease(first.Id, false));
        CoverUrlOf((await context.Releases.AsNoTracking().SingleAsync(r => r.Id == first.Id)).ProviderPayloadsJson)
            .ShouldBe("https://covers.openlibrary.org/b/id/42-L.jpg");
    }

    [Fact]
    public async Task A_provider_answer_with_no_format_is_stored_as_unknown_not_other()
    {
        await using var context = Db.CreateContext();
        var store = new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System);

        var ensured = await store.EnsureAsync(Book(null, format: null), Library, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        (await context.Releases.AsNoTracking().SingleAsync(r => r.Id == ensured.Id)).Format.ShouldBe(ReleaseFormat.Unknown);
    }

    [Fact]
    public async Task A_provider_answer_with_an_uncategorized_format_keeps_other()
    {
        await using var context = Db.CreateContext();
        var store = new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System);

        var ensured = await store.EnsureAsync(Book(null, format: ReleaseFormat.Other), Library, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync();

        (await context.Releases.AsNoTracking().SingleAsync(r => r.Id == ensured.Id)).Format.ShouldBe(ReleaseFormat.Other);
    }

    [Fact]
    public async Task Same_title_and_year_books_by_different_authors_get_different_works()
    {
        await using var context = Db.CreateContext();
        var ids = new FakeIdGenerator();
        var cancellationToken = TestContext.Current.CancellationToken;

        var mayberry = await new ReleaseStore(context, ids, TimeProvider.System)
            .EnsureAsync(Novel("9780306406157", "OL1M", "Sarah Mayberry"), Library, cancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var kleypas = await new ReleaseStore(context, ids, TimeProvider.System)
            .EnsureAsync(Novel("9780061990625", "OL2M", "Lisa Kleypas"), Library, cancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        kleypas.IsNew.ShouldBeTrue();
        var works = await context.Releases.AsNoTracking()
            .Where(r => r.Id == mayberry.Id || r.Id == kleypas.Id)
            .Select(r => new { r.Id, r.WorkId })
            .ToDictionaryAsync(r => r.Id, cancellationToken);
        works[kleypas.Id].WorkId.ShouldNotBe(works[mayberry.Id].WorkId);
        CreatorsOf((await context.Works.AsNoTracking().SingleAsync(w => w.Id == works[kleypas.Id].WorkId, cancellationToken)).CreatorsJson)
            .ShouldBe(["Lisa Kleypas"]);
    }

    [Fact]
    public async Task Same_request_answers_by_different_authors_get_different_works()
    {
        await using var context = Db.CreateContext();
        var store = new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System);
        var cancellationToken = TestContext.Current.CancellationToken;

        var mayberry = await store.EnsureAsync(Novel("9780306406157", "OL1M", "Sarah Mayberry"), Library, cancellationToken);
        var kleypas = await store.EnsureAsync(Novel("9780061990625", "OL2M", "Lisa Kleypas"), Library, cancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var works = await context.Releases.AsNoTracking()
            .Where(r => r.Id == mayberry.Id || r.Id == kleypas.Id)
            .Select(r => new { r.Id, r.WorkId })
            .ToDictionaryAsync(r => r.Id, cancellationToken);
        kleypas.Id.ShouldNotBe(mayberry.Id);
        works[kleypas.Id].WorkId.ShouldNotBe(works[mayberry.Id].WorkId);
    }

    [Fact]
    public async Task Same_title_year_and_author_books_share_one_release()
    {
        await using var context = Db.CreateContext();
        var ids = new FakeIdGenerator();
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await new ReleaseStore(context, ids, TimeProvider.System)
            .EnsureAsync(Novel("9780306406157", "OL1M", "Sarah Mayberry"), Library, cancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var again = await new ReleaseStore(context, ids, TimeProvider.System)
            .EnsureAsync(Novel("9780061990625", "OL3M", "Sarah Mayberry"), Library, cancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        again.ShouldBe(new EnsuredRelease(first.Id, false));
    }

    [Fact]
    public async Task A_provider_answer_naming_no_author_reuses_the_same_title_work()
    {
        await using var context = Db.CreateContext();
        var ids = new FakeIdGenerator();
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await new ReleaseStore(context, ids, TimeProvider.System)
            .EnsureAsync(Novel("9780306406157", "OL1M", "Sarah Mayberry"), Library, cancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var again = await new ReleaseStore(context, ids, TimeProvider.System)
            .EnsureAsync(Novel("9780061990625", "OL4M"), Library, cancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        again.ShouldBe(new EnsuredRelease(first.Id, false));
    }
}
