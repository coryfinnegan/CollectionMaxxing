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

    public Task<CoverFetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        Asked.Add(url);
        return Task.FromResult(Fails
            ? new CoverFetchResult(null, CoverFailure.FetchFailed)
            : new CoverFetchResult(new FetchedCover(Jpeg, "image/jpeg"), null));
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

    private static string ItemsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/items";

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

    private async Task<Guid> SharedReleaseWithCoverUrlAsync(string title, string isbn)
    {
        await using var context = Db.CreateContext();
        var work = new Domain.Catalog.Work
        {
            Id = Guid.CreateVersion7(),
            Kind = WorkKind.Book,
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            NormalizedTitle = title.ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = work.Id,
            Format = ReleaseFormat.Paperback,
            Source = ReleaseSource.Provider,
            ProviderPayloadsJson = System.Text.Json.JsonSerializer.Serialize(
                new { provider = "openlibrary", externalId = "OL1M", coverUrl = CoverUrl }),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.Works.Add(work);
        context.Releases.Add(release);
        context.ReleaseIdentifiers.Add(new ReleaseIdentifier
        {
            ReleaseId = release.Id,
            Type = IdentifierType.Isbn13,
            Value = Domain.Identifiers.Identifier.TryParse(isbn, out var id) ? id!.Value : isbn,
        });
        await context.SaveChangesAsync();
        return release.Id;
    }

    private async Task<(HttpClient Client, TestLibrary Library, Guid SessionId)> ScanBookAsync(string? coverUrl = CoverUrl)
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
            CoverUrl = coverUrl,
            Identifiers = [Identifier.TryParse(ScanData.Isbn, out var isbn) ? isbn! : throw new InvalidOperationException()],
        };
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id); // these tests pin covers at commit, not auto-add (#341)
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

    [Fact]
    public async Task A_committed_book_shows_its_cover_in_the_library_grid_and_item_detail()
    {
        var (client, library, sessionId) = await ScanBookAsync();
        using var _ = client;

        (await CommitAsync(client, sessionId)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        await using var context = Db.CreateContext();
        var release = await ReleaseOfAsync(context);
        var expected = $"/media/covers/{release.CoverAssetId}";
        var itemId = (await context.Items.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == library.Id)).Id;

        var grid = await ReadJsonAsync(await client.GetAsync(ItemsUrl(library.Id)));
        grid.GetProperty("items").EnumerateArray().Single().GetProperty("coverUrl").GetString().ShouldBe(expected);

        var detail = await ReadJsonAsync(await client.GetAsync($"{ItemsUrl(library.Id)}/{itemId}"));
        detail.GetProperty("release").GetProperty("coverUrl").GetString().ShouldBe(expected);

        var cover = await client.GetAsync(expected);
        cover.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        cover.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task A_cover_that_cannot_be_fetched_is_recorded_on_the_release()
    {
        _covers.Fails = true;
        var (client, library, sessionId) = await ScanBookAsync();
        using var _ = client;

        (await CommitAsync(client, sessionId)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        await using var context = Db.CreateContext();
        var (status, failure) = CoverOutcomeOf((await ReleaseOfAsync(context)).ProviderPayloadsJson);
        status.ShouldBe("failed");
        failure.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_book_whose_provider_names_no_cover_is_recorded_as_having_none()
    {
        var (client, library, sessionId) = await ScanBookAsync(coverUrl: null);
        using var _ = client;

        (await CommitAsync(client, sessionId)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        _covers.Asked.ShouldBeEmpty();
        await using var context = Db.CreateContext();
        var release = await ReleaseOfAsync(context);
        release.CoverAssetId.ShouldBeNull();
        var (status, failure) = CoverOutcomeOf(release.ProviderPayloadsJson);
        status.ShouldBe("no-cover");
        failure.ShouldBeNull();
    }

    [Fact]
    public async Task A_second_commit_for_the_same_release_adds_no_second_cover_asset()
    {
        var (client, library, firstSession) = await ScanBookAsync();
        using var _ = client;
        (await CommitAsync(client, firstSession)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        var secondSession = await StartSessionAsync(client, library.Id);
        var candidate = (await ScanAsync(client, secondSession, ScanData.Isbn))[0];
        Resolution(candidate).ShouldBe("matched");
        (await DecideAsync(client, secondSession, Id(candidate), new { decision = "accepted" })).StatusCode
            .ShouldBe(System.Net.HttpStatusCode.OK);
        (await CommitAsync(client, secondSession)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        await using var context = Db.CreateContext();
        (await context.Assets.IgnoreQueryFilters().CountAsync(a => a.Kind == AssetKind.Cover)).ShouldBe(1);
    }

    [Fact]
    public async Task Adding_a_release_by_search_fetches_its_cover()
    {
        var library = await CreateLibraryAsync();
        using var client = As(library, LibraryRole.Editor);
        var releaseId = await SharedReleaseWithCoverUrlAsync("Searchable Book", "9780140328721");

        var request = new HttpRequestMessage(HttpMethod.Post, ItemsUrl(library.Id)) { Content = Json(new { releaseId }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await client.SendAsync(request)).StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);

        _covers.Asked.ShouldBe([CoverUrl]);
        await using var context = Db.CreateContext();
        (await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == releaseId)).CoverAssetId.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_edited_edition_keeps_the_cover_of_the_release_it_came_from()
    {
        var (client, library, sessionId) = await ScanBookAsync();
        using var _ = client;
        var candidate = (await GetSessionAsync(client, sessionId)).GetProperty("candidates").EnumerateArray().ToList()[0];

        (await DecideAsync(client, sessionId, Id(candidate), new { edition = "Revised" })).StatusCode
            .ShouldBe(System.Net.HttpStatusCode.OK);
        (await CommitAsync(client, sessionId)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        await using var context = Db.CreateContext();
        var origin = await ReleaseOfAsync(context);
        origin.CoverAssetId.ShouldNotBeNull("the release the person saw keeps its own cover too");
        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == library.Id);
        var edited = await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == item.ReleaseId);
        edited.Id.ShouldNotBe(origin.Id);
        edited.OwnerLibraryId.ShouldBe(library.Id);
        edited.CoverAssetId.ShouldBe(origin.CoverAssetId);
    }
}
