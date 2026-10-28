using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// Issue #516 (prod 19:30:55Z Suddenly You picture scan): an unowned Sarah
// Mayberry same-title book ranked first above the twice-owned Lisa Kleypas
// novel, and the bind took alternatives[0].
//
// Pre-fix, this seed answered (ambiguous, T2, undecided, candidate owned 0):
//   [0] Mayberry 1.0 owned 0  <- ranked first by scorer-order noise
//   [1] Kleypas learned 1.0 owned 2
//   [2] Kleypas sibling 1.0 owned 0
// Post-fix, the owned novel ranks first with its count; the verdict and every
// score stay exactly as the scorer computed them.
public class ScanIdentifyOwnedSameTitleTests(ApiFixture api) : IdentifyTest(api)
{
    private const string Title = "Suddenly You";
    private const string Kleypas = "Lisa Kleypas";
    private const string Mayberry = "Sarah Mayberry";

    private sealed record Seed(HttpClient Client, Guid SessionId, Guid MayberryId, Guid LearnedId, Guid SiblingId);

    private static Identifier IsbnOf(string value)
    {
        Identifier.TryParse(value, out var id).ShouldBeTrue();
        return id!;
    }

    private async Task<Seed> SeedSuddenlyYouAsync()
    {
        var library = await CreateLibraryAsync();
        var (_, mayberryId) = await CatalogSeed.AddAsync(
            Db, Title, WorkKind.Book, 2012, format: ReleaseFormat.Unknown, creators: [Mayberry]);
        var (_, learnedId) = await CatalogSeed.AddAsync(
            Db, Title, WorkKind.Book, 2003, barcode: "9780061178986", barcodeType: IdentifierType.Isbn13,
            format: ReleaseFormat.Unknown, creators: [Kleypas],
            providerPayloads: """{"provider":"openlibrary","externalId":"OL25816478W","coverUrl":null,"coverStatus":"no-cover"}""");
        var assetId = await CatalogSeed.AddAssetAsync(Db, "covers/ab/sibling.png");
        var (_, siblingId) = await CatalogSeed.AddAsync(
            Db, Title, WorkKind.Book, 2001, format: ReleaseFormat.Unknown, creators: [Kleypas], coverAssetId: assetId);
        await TestData.CreateItemAsync(Db, library, learnedId);
        await TestData.CreateItemAsync(Db, library, learnedId);
        Books.Results.Add(new ProviderRelease
        {
            Provider = "openlibrary",
            ExternalId = "OL999W",
            WorkKey = "OL999W",
            Title = Title,
            Kind = WorkKind.Book,
            Year = 2005,
            Creators = [Kleypas],
            CoverUrl = "https://covers.openlibrary.org/b/id/7632416-L.jpg",
            Identifiers = [IsbnOf("9780061259357")],
        });
        Books.Results.Add(new ProviderRelease
        {
            Provider = "openlibrary",
            ExternalId = "OL17465093W",
            WorkKey = "OL17465093W",
            Title = Title,
            Kind = WorkKind.Book,
            Year = 2012,
            Creators = [Mayberry],
            CoverUrl = "https://covers.openlibrary.org/b/id/9999999-L.jpg",
            Identifiers = [IsbnOf("9780373718122")],
        });
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);
        return new Seed(client, sessionId, mayberryId, learnedId, siblingId);
    }

    private static async Task<JsonElement> IdentifyOnceAsync(HttpClient client, Guid sessionId, object item)
    {
        var body = Body(Guid.NewGuid(), [item]);
        body["kind"] = "shelf";
        body["shotKind"] = "shelf_segment";
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();
    }

    [Fact]
    public async Task An_owned_same_title_book_ranks_first_in_a_close_call()
    {
        var seed = await SeedSuddenlyYouAsync();

        var candidate = await IdentifyOnceAsync(seed.Client, seed.SessionId, Read(title: Title, kind: "book"));

        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("tier").GetString().ShouldBe("T2");
        Decision(candidate).ShouldBe("undecided");
        var alternatives = candidate.GetProperty("alternatives").EnumerateArray().ToList();
        alternatives.Count.ShouldBe(3);
        alternatives[0].GetProperty("releaseId").GetGuid().ShouldBe(seed.LearnedId);
        alternatives[0].GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(2);
        alternatives.Select(a => a.GetProperty("releaseId").GetGuid()).ShouldContain(seed.MayberryId);
        // The verdict and every score stay exactly as the scorer computed them; only the order changes.
        alternatives.All(a => a.GetProperty("score").GetDouble() == 1.0).ShouldBeTrue();

        await using var context = Db.CreateContext();
        var trace = (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(candidate))).TraceJson;
        trace.ShouldNotBeNull();
        trace.ShouldContain("ownership");
        trace.ShouldContain("biased");
    }

    [Fact]
    public async Task A_pick_of_a_non_first_alternative_binds_it_not_the_rank_first()
    {
        var seed = await SeedSuddenlyYouAsync();
        var candidate = await IdentifyOnceAsync(seed.Client, seed.SessionId, Read(title: Title, kind: "book"));
        var alternatives = candidate.GetProperty("alternatives").EnumerateArray().ToList();
        alternatives[0].GetProperty("releaseId").GetGuid().ShouldNotBe(seed.MayberryId);

        // The owner taps the unowned Mayberry row (not rank-first): the pick binds, like Review's staged pick.
        var decide = await DecideAsync(seed.Client, seed.SessionId, Id(candidate), new { releaseId = seed.MayberryId });
        decide.StatusCode.ShouldBe(HttpStatusCode.OK, await decide.Content.ReadAsStringAsync());
        var picked = await ReadJsonAsync(decide);
        picked.GetProperty("resolution").GetString().ShouldBe("matched");
        picked.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(seed.MayberryId);

        var commit = await CommitAsync(seed.Client, seed.SessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());

        await using var context = Db.CreateContext();
        var libraryId = (await context.ScanSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == seed.SessionId)).LibraryId;
        var bound = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == libraryId && i.ReleaseId == seed.MayberryId);
        bound.ReleaseId.ShouldBe(seed.MayberryId);
    }

    [Fact]
    public async Task A_clear_lead_still_matches_the_best_release_despite_an_owned_runner_up()
    {
        var seed = await SeedSuddenlyYouAsync();

        // The read names Mayberry's year: a genuine signal, not noise (1.0 vs 0.9, past the 0.08 lead rule).
        var candidate = await IdentifyOnceAsync(seed.Client, seed.SessionId, Read(title: Title, kind: "book", year: 2012));

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(seed.MayberryId);
        var alternatives = candidate.GetProperty("alternatives").EnumerateArray().ToList();
        alternatives[0].GetProperty("releaseId").GetGuid().ShouldBe(seed.MayberryId);
        var owned = alternatives.Single(a => a.GetProperty("releaseId").GetGuid() == seed.LearnedId);
        owned.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(2);
    }
}
