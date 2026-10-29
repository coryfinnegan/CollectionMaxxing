using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Scanning;

// Issue #450: a barcode/ISBN add resolves the edition AND sets its cover in the same operation, even when the
// add is a T0 auto-add rather than a full commit. When the first catalog names no cover, the other catalog's
// cover wins; when none names one, the item is still added and the release says why.
public class ScanAutoCommitCoverTests : ScanTest
{
    private const string CoverUrl = "https://covers.openlibrary.org/b/id/42-L.jpg";
    private const string GoogleCoverUrl = "https://books.google.com/books/content?id=abc&img=1&zoom=1";

    // The issue's repro, with an invented book in place of the owner's: Open Library knows the edition but names no cover for it.
    private const string ReproIsbn = "9781234567880";
    private const string ReproTitle = "Lantern Road Graphic Novel: The Long Winter, Part One of Three";

    private readonly FakeCoverFetcher _covers = new();
    private readonly FakeProvider _google = new("googlebooks");

    public ScanAutoCommitCoverTests(ApiFixture api) : base(api)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICoverFetcher>();
            services.AddSingleton<ICoverFetcher>(_covers);
            services.AddSingleton<ICatalogProvider>(_google);
        };
    }

    private static async Task<Release> ReleaseOfAsync(DbContext context, string isbn) =>
        await (from i in context.Set<ReleaseIdentifier>() join r in context.Set<Release>() on i.ReleaseId equals r.Id where i.Value == isbn select r).SingleAsync();

    private static (string? Status, string? Failure) CoverOutcomeOf(string? payloadJson)
    {
        if (payloadJson is null)
        {
            return (null, null);
        }

        using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        var status = root.TryGetProperty("coverStatus", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
            ? s.GetString() : null;
        var failure = root.TryGetProperty("coverFailure", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String
            ? f.GetString() : null;
        return (status, failure);
    }

    private static ProviderRelease Book(string provider, string externalId, string title, string isbn, string? coverUrl) => new()
    {
        Provider = provider,
        ExternalId = externalId,
        Title = title,
        Kind = WorkKind.Book,
        Format = ReleaseFormat.Paperback,
        CoverUrl = coverUrl,
        Identifiers = [Identifier.TryParse(isbn, out var id) ? id! : throw new InvalidOperationException()],
    };

    private static Guid? CreatedItemId(System.Text.Json.JsonElement candidate) =>
        candidate.TryGetProperty("createdItemId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetGuid() : null;

    [Fact]
    public async Task A_book_auto_added_by_barcode_gets_the_cover_its_provider_named()
    {
        var library = await CreateLibraryAsync();
        Books.LookupResult = Book("openlibrary", "OL1M", "Dune", ScanData.Isbn, CoverUrl);
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(client, library.Id);

        var candidate = (await ScanAsync(client, sessionId, ScanData.Isbn))[0];

        Resolution(candidate).ShouldBe("matched");
        CreatedItemId(candidate).ShouldNotBeNull();
        _covers.Asked.ShouldBe([CoverUrl]);
        await using var context = Db.CreateContext();
        var release = await ReleaseOfAsync(context, ScanData.Isbn);
        release.CoverAssetId.ShouldNotBeNull();
        CoverOutcomeOf(release.ProviderPayloadsJson).Status.ShouldBe("fetched");
    }

    [Fact]
    public async Task An_auto_add_uses_the_other_catalogs_cover_when_the_first_names_none()
    {
        var library = await CreateLibraryAsync();
        Books.LookupResult = Book("openlibrary", "OL1M", "Dune", ScanData.Isbn, coverUrl: null);
        _google.LookupResult = Book("googlebooks", "abc", "Dune", ScanData.Isbn, GoogleCoverUrl);
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(client, library.Id);

        var candidate = (await ScanAsync(client, sessionId, ScanData.Isbn))[0];

        Resolution(candidate).ShouldBe("matched");
        CreatedItemId(candidate).ShouldNotBeNull();
        _covers.Asked.ShouldBe([GoogleCoverUrl]);
        await using var context = Db.CreateContext();
        var release = await ReleaseOfAsync(context, ScanData.Isbn);
        release.CoverAssetId.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_auto_add_without_a_cover_anywhere_is_added_and_recorded_as_having_none()
    {
        var library = await CreateLibraryAsync();
        Books.LookupResult = Book("openlibrary", "OL61859789M", ReproTitle, ReproIsbn, coverUrl: null);
        _google.LookupResult = Book("googlebooks", "abc", ReproTitle, ReproIsbn, coverUrl: null);
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(client, library.Id);

        var candidate = (await ScanAsync(client, sessionId, ReproIsbn))[0];

        Resolution(candidate).ShouldBe("matched");
        CreatedItemId(candidate).ShouldNotBeNull();
        (await CountItemsAsync(library.Id)).ShouldBe(1);
        _covers.Asked.ShouldBeEmpty();
        await using var context = Db.CreateContext();
        var release = await ReleaseOfAsync(context, ReproIsbn);
        release.CoverAssetId.ShouldBeNull();
        var (status, failure) = CoverOutcomeOf(release.ProviderPayloadsJson);
        status.ShouldBe("no-cover");
        failure.ShouldBeNull();
    }
}
