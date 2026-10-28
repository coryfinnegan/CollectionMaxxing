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
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Scanning;

// Issue #490 (regression on #450, first fixed by PR #463): the owner's barcode add landed coverless because the only
// catalog consulted (Open Library) names no cover for it while the catalog holding the cover (Google Books) was
// silently switched off (no key). The scan-time verdict must not be terminal: once the missing catalog comes online,
// the next night's backfill heals the release, and later scans land on the healed release.
public class ScanBarcodeCoverHealingTests : ScanTest
{
    // The owner's repro from the issue, pinned.
    private const string ReproIsbn = "9780316323888";
    private const string ReproTitle = "Home: recipes to cook with family and friends";
    private const string GoogleCoverUrl = "https://books.google.com/books/content?id=YcDgoQEACAAJ&img=1&zoom=1";

    private readonly FakeCoverFetcher _covers = new();
    private readonly FakeProvider _google = new("googlebooks") { Enabled = false };
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public ScanBarcodeCoverHealingTests(ApiFixture api) : base(api)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICoverFetcher>();
            services.AddSingleton<ICoverFetcher>(_covers);
            services.AddSingleton<ICatalogProvider>(_google);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
        };
    }

    private static ProviderRelease Book(string provider, string externalId, string? coverUrl) => new()
    {
        Provider = provider,
        ExternalId = externalId,
        Title = ReproTitle,
        Kind = WorkKind.Book,
        Format = ReleaseFormat.Paperback,
        CoverUrl = coverUrl,
        Identifiers = [Identifier.TryParse(ReproIsbn, out var id) ? id! : throw new InvalidOperationException()],
    };

    private static async Task<Release> ReleaseOfAsync(DbContext context, string isbn) =>
        await (from i in context.Set<ReleaseIdentifier>() join r in context.Set<Release>() on i.ReleaseId equals r.Id where i.Value == isbn select r).IgnoreQueryFilters().SingleAsync();

    private static async Task<Release> ReleaseByIdAsync(DbContext context, Guid releaseId) =>
        await context.Set<Release>().IgnoreQueryFilters().SingleAsync(r => r.Id == releaseId);

    private static string? CoverStatusOf(string? payloadJson)
    {
        if (payloadJson is null)
        {
            return null;
        }

        using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty("coverStatus", out var status)
            && status.ValueKind == System.Text.Json.JsonValueKind.String
            ? status.GetString()
            : null;
    }

    private static Guid? CreatedItemId(System.Text.Json.JsonElement candidate) =>
        candidate.TryGetProperty("createdItemId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetGuid() : null;

    private async Task BackfillAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BackfillCovers>()
            .HandleAsync(new BackfillCovers.Job(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_barcode_add_coverless_while_a_catalog_is_off_heals_once_that_catalog_comes_online()
    {
        var library = await CreateLibraryAsync();
        Books.LookupResult = Book("openlibrary", "OL27185406M", coverUrl: null);
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(client, library.Id);

        // Tonight's path: a T0 auto-add while Google Books is off. Added without a cover, and says why.
        var candidate = (await ScanAsync(client, sessionId, ReproIsbn))[0];
        Resolution(candidate).ShouldBe("matched");
        var firstItemId = CreatedItemId(candidate);
        firstItemId.ShouldNotBeNull();
        _covers.Asked.ShouldBeEmpty();
        Guid releaseId;
        await using (var context = Db.CreateContext())
        {
            var release = await ReleaseOfAsync(context, ReproIsbn);
            release.CoverAssetId.ShouldBeNull();
            CoverStatusOf(release.ProviderPayloadsJson).ShouldBe("no-cover");
            releaseId = release.Id;
        }

        // The key lands and the night passes: the backfill re-asks and the release gains Google's cover.
        _google.Enabled = true;
        _google.LookupResult = Book("googlebooks", "YcDgoQEACAAJ", GoogleCoverUrl);
        _clock.Advance(TimeSpan.FromHours(25));
        await BackfillAsync();

        _covers.Asked.ShouldBe([GoogleCoverUrl]);
        await using (var context = Db.CreateContext())
        {
            var healed = await ReleaseByIdAsync(context, releaseId);
            healed.CoverAssetId.ShouldNotBeNull();
            CoverStatusOf(healed.ProviderPayloadsJson).ShouldBe("fetched");
        }

        // The auto-added item now shows the cover in the Library grid and item detail.
        await using (var context = Db.CreateContext())
        {
            var healed = await ReleaseByIdAsync(context, releaseId);
            var expected = $"/media/covers/{healed.CoverAssetId}";
            var itemId = (await context.Items.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == library.Id)).Id;

            var grid = await ReadJsonAsync(await client.GetAsync(ItemsUrl(library.Id)));
            grid.GetProperty("items").EnumerateArray().Single().GetProperty("coverUrl").GetString().ShouldBe(expected);

            var detail = await ReadJsonAsync(await client.GetAsync($"{ItemsUrl(library.Id)}/{itemId}"));
            detail.GetProperty("release").GetProperty("coverUrl").GetString().ShouldBe(expected);
        }
    }

    private static string ItemsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/items";
}
