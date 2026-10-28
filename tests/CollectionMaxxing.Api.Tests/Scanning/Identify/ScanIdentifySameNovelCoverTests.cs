using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// Issue #514 (prod 18:02 UTC Suddenly You rescan, ISBN 9780061178986): a picture
// rescan matched the learned coverless release while the same answer carried the
// same novel's cover on a parallel release row; the item bound the coverless row
// and the covered answer orphaned. A rescan must end with the item on a covered
// release and mint no fresh parallel row for the same novel. A same-title book by
// a different author (prod: Sarah Mayberry's "Suddenly You") is a different work:
// it keeps its own row and never donates its cover.
public class ScanIdentifySameNovelCoverTests : IdentifyTest
{
    private const string Isbn = "9780061178986";
    private const string Title = "Suddenly You";
    private const string Author = "Lisa Kleypas";
    private const string LearnedExternalId = "OL25816478W";
    private const string SameNovelCover = "https://covers.openlibrary.org/b/id/7632416-L.jpg";
    private const string SameNovelIsbn = "9780061259357";
    private const string OtherBookCover = "https://covers.openlibrary.org/b/id/9999999-L.jpg";
    private const string OtherBookIsbn = "9780373718122";

    private readonly FakeCoverFetcher _covers = new();

    public ScanIdentifySameNovelCoverTests(ApiFixture api) : base(api)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICoverFetcher>();
            services.AddSingleton<ICoverFetcher>(_covers);
        };
    }

    private static ProviderRelease SameNovelDoc() => new()
    {
        Provider = "openlibrary",
        ExternalId = "OL999W",
        WorkKey = "OL999W",
        Title = Title,
        Kind = WorkKind.Book,
        Year = 2005,
        Creators = [Author],
        CoverUrl = SameNovelCover,
        Identifiers = [IsbnOf(SameNovelIsbn)],
    };

    private static ProviderRelease OtherAuthorDoc() => new()
    {
        Provider = "openlibrary",
        ExternalId = "OL17465093W",
        WorkKey = "OL17465093W",
        Title = Title,
        Kind = WorkKind.Book,
        Year = 2012,
        Creators = ["Sarah Mayberry"],
        CoverUrl = OtherBookCover,
        Identifiers = [IsbnOf(OtherBookIsbn)],
    };

    private static Identifier IsbnOf(string value)
    {
        Identifier.TryParse(value, out var id).ShouldBeTrue();
        return id!;
    }

    private static string? CoverUrlOf(string? payloadJson) => TextOf(payloadJson, "coverUrl");

    private static string? CoverStatusOf(string? payloadJson) => TextOf(payloadJson, "coverStatus");

    private static string? TextOf(string? payloadJson, string property)
    {
        if (payloadJson is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static List<string> CreatorsOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind != JsonValueKind.Array
            ? []
            : [.. document.RootElement.EnumerateArray()
                .Select(e => e.GetString())
                .OfType<string>()];
    }

    [Fact]
    public async Task A_rescan_binds_the_learned_release_covered_and_mints_no_parallel_same_novel_row()
    {
        var library = await CreateLibraryAsync();
        var (_, learnedId) = await CatalogSeed.AddAsync(
            Db, Title, WorkKind.Book, 2003, barcode: Isbn, barcodeType: IdentifierType.Isbn13,
            format: ReleaseFormat.Unknown, creators: [Author],
            providerPayloads: """{"provider":"openlibrary","externalId":"OL25816478W","coverUrl":null,"coverStatus":"no-cover"}""");
        Books.Results.Add(SameNovelDoc());
        Books.Results.Add(OtherAuthorDoc());
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var body = Body(Guid.NewGuid(), [Read(title: Title, kind: "book")]);
        body["kind"] = "shelf";
        body["shotKind"] = "shelf_segment";
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("tier").GetString().ShouldBe("T2");
        var alternatives = candidate.GetProperty("alternatives").EnumerateArray().ToList();
        alternatives.Count.ShouldBe(3);
        alternatives[0].GetProperty("releaseId").GetGuid().ShouldBe(learnedId);
        alternatives[1].GetProperty("releaseId").GetGuid().ShouldBe(learnedId);

        (await DecideAsync(client, sessionId, Id(candidate), new { releaseId = learnedId })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var commit = await CommitAsync(client, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());

        // The donated cover is fetched once at commit, like any named cover.
        _covers.Asked.ShouldBe([SameNovelCover]);

        await using var context = Db.CreateContext();
        var releases = await context.Releases.IgnoreQueryFilters().OrderBy(r => r.CreatedAt).ToListAsync();
        releases.Count.ShouldBe(2);

        var learned = releases.Single(r => r.Id == learnedId);
        learned.CoverAssetId.ShouldNotBeNull();
        learned.ProviderPayloadsJson.ShouldNotBeNull();
        CoverStatusOf(learned.ProviderPayloadsJson).ShouldBe("fetched");
        CoverUrlOf(learned.ProviderPayloadsJson).ShouldBe(SameNovelCover);
        learned.ProviderPayloadsJson.ShouldContain(LearnedExternalId);
        learned.ProviderPayloadsJson.ShouldNotContain("OL999W");

        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == library.Id);
        item.ReleaseId.ShouldBe(learnedId);

        // The other author's book keeps its own row, cover and ISBNs: a different
        // work is a legitimate alternative, never a cover donor.
        var other = releases.Single(r => r.Id != learnedId);
        CoverUrlOf(other.ProviderPayloadsJson).ShouldBe(OtherBookCover);
        var otherWork = await context.Works.IgnoreQueryFilters().SingleAsync(w => w.Id == other.WorkId);
        CreatorsOf(otherWork.CreatorsJson).ShouldBe(["Sarah Mayberry"]);
        var otherIdentifiers = await context.ReleaseIdentifiers.IgnoreQueryFilters()
            .Where(i => i.ReleaseId == other.Id).Select(i => i.Value).ToListAsync();
        otherIdentifiers.ShouldBe([OtherBookIsbn]);

        // The ISBN link stays on the learned edition: no rebind, no pollution.
        var isbnLinks = await context.ReleaseIdentifiers.IgnoreQueryFilters()
            .Where(i => i.Value == Isbn).Select(i => i.ReleaseId).ToListAsync();
        isbnLinks.ShouldBe([learnedId]);
    }

    [Fact]
    public async Task A_rescan_shares_a_same_novel_release_cover_without_downloading_it_again()
    {
        var library = await CreateLibraryAsync();
        var assetId = await CatalogSeed.AddAssetAsync(Db, "covers/ab/asset.png");
        await CatalogSeed.AddAsync(
            Db, Title, WorkKind.Book, 2001, format: ReleaseFormat.Unknown, creators: [Author], coverAssetId: assetId);
        var (_, learnedId) = await CatalogSeed.AddAsync(
            Db, Title, WorkKind.Book, 2003, barcode: Isbn, barcodeType: IdentifierType.Isbn13,
            format: ReleaseFormat.Unknown, creators: [Author],
            providerPayloads: """{"provider":"openlibrary","externalId":"OL25816478W","coverUrl":null,"coverStatus":"no-cover"}""");
        Books.Results.Add(OtherAuthorDoc());
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var body = Body(Guid.NewGuid(), [Read(title: Title, kind: "book")]);
        body["kind"] = "shelf";
        body["shotKind"] = "shelf_segment";
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        Resolution(candidate).ShouldBe("ambiguous");
        var alternatives = candidate.GetProperty("alternatives").EnumerateArray().ToList();
        alternatives.Count.ShouldBe(3);
        alternatives.Select(a => a.GetProperty("releaseId").GetGuid()).ShouldContain(learnedId);

        (await DecideAsync(client, sessionId, Id(candidate), new { releaseId = learnedId })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var commit = await CommitAsync(client, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());

        // The cover asset already exists: shared, never downloaded again.
        _covers.Asked.ShouldBeEmpty();

        await using var context = Db.CreateContext();
        var learned = await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == learnedId);
        learned.CoverAssetId.ShouldBe(assetId);
        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == library.Id);
        item.ReleaseId.ShouldBe(learnedId);
    }
}
