using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Scanning;

/// <summary>A cover fetcher that answers from a table instead of the internet.</summary>
internal sealed class FakeCoverFetcher : ICoverFetcher
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    public List<string> Asked { get; } = [];

    public bool Fails { get; set; }

    public Task<FetchedCover?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        Asked.Add(url);
        return Task.FromResult<FetchedCover?>(Fails ? null : new FetchedCover(Jpeg, "image/jpeg"));
    }
}

// A scanned book's cover is the one its provider named: the commit downloads it once and the release keeps it.
public class ScanCoverTests : ScanTest
{
    private const string CoverUrl = "https://covers.openlibrary.org/b/id/42-L.jpg";
    private readonly FakeCoverFetcher _covers = new();

    public ScanCoverTests(ApiFixture api) : base(api)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICoverFetcher>();
            services.AddSingleton<ICoverFetcher>(_covers);
        };
    }

    private static async Task<Release> ReleaseOfAsync(CollectionMaxxing.Infrastructure.Persistence.AppDbContext context) =>
        await (from i in context.ReleaseIdentifiers join r in context.Releases on i.ReleaseId equals r.Id where i.Value == ScanData.Isbn select r).SingleAsync();

    private async Task<(HttpClient Client, TestLibrary Library, Guid SessionId)> ScanBookAsync()
    {
        var library = await CreateLibraryAsync();
        Books.LookupResult = new()
        {
            Provider = "openlibrary",
            ExternalId = "OL1M",
            Title = "Dune",
            Kind = WorkKind.Book,
            Format = ReleaseFormat.Paperback,
            Year = 1965,
            CoverUrl = CoverUrl,
            Identifiers = [Identifier.TryParse(ScanData.Isbn, out var isbn) ? isbn! : throw new InvalidOperationException()],
        };
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(client, library.Id);
        await ScanAsync(client, sessionId, ScanData.Isbn);
        return (client, library, sessionId);
    }

    [Fact]
    public async Task A_book_added_by_barcode_gets_the_cover_its_provider_named()
    {
        var (client, library, sessionId) = await ScanBookAsync();
        using var _ = client;

        (await CommitAsync(client, sessionId)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        _covers.Asked.ShouldBe([CoverUrl]);
        await using var context = Db.CreateContext();
        var release = await ReleaseOfAsync(context);
        release.CoverAssetId.ShouldNotBeNull();
        var asset = await context.Assets.SingleAsync(a => a.Id == release.CoverAssetId);
        asset.Kind.ShouldBe(AssetKind.Cover);
        asset.ContentType.ShouldBe("image/jpeg");
        asset.OwnerLibraryId.ShouldBeNull();
        asset.SourceUrl.ShouldBe(CoverUrl);
    }

    [Fact]
    public async Task A_cover_that_cannot_be_fetched_does_not_stop_the_book_being_added()
    {
        _covers.Fails = true;
        var (client, library, sessionId) = await ScanBookAsync();
        using var _ = client;

        (await CommitAsync(client, sessionId)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        (await CountItemsAsync(library.Id)).ShouldBe(1);
        await using var context = Db.CreateContext();
        (await ReleaseOfAsync(context)).CoverAssetId.ShouldBeNull();
    }
}
