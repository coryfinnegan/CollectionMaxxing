using System.Text.Json;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Artwork;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.Infrastructure.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>
/// Stage 2 (#438): a cover capture verified against the alternatives' covers re-ranks them by the combined score and
/// preselects a clear visual match for Review. The verifier and the cover downloads are fakes; the real SIFT comparison
/// is covered by the fixture tests once OpenCvSharp lands.
/// </summary>
public class VerifyCoverArtworkTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    private sealed class StubCovers(Dictionary<string, byte[]> covers) : ICoverFetcher
    {
        public List<string> Fetched { get; } = [];

        public Task<CoverFetchResult> FetchAsync(string url, CancellationToken cancellationToken)
        {
            Fetched.Add(url);
            return Task.FromResult(covers.TryGetValue(url, out var bytes)
                ? new CoverFetchResult(new FetchedCover(bytes, "image/jpeg"), null)
                : new CoverFetchResult(null, CoverFailure.FetchFailed));
        }
    }

    private sealed class StubVerifier(Func<byte[], byte[], int> inliers) : ICoverVerifier
    {
        public CoverQuad? SeenQuad { get; private set; }

        public Task<CoverComparison> CompareAsync(ReadOnlyMemory<byte> capture, ReadOnlyMemory<byte> cover, CoverQuad? quad, CancellationToken ct)
        {
            SeenQuad = quad;
            var n = inliers(capture.ToArray(), cover.ToArray());
            return Task.FromResult(new CoverComparison(n, 200, 150, 12));
        }
    }

    private sealed class StubCoverStore(Dictionary<string, byte[]> blobs) : ICoverStore
    {
        public Task<Stream?> OpenReadAsync(string blobPath, CancellationToken cancellationToken) =>
            Task.FromResult<Stream?>(blobs.TryGetValue(blobPath, out var bytes) ? new MemoryStream(bytes) : null);
    }

    private sealed class FixedScoring(ScoringOptions value) : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue => value;

        public ScoringOptions Get(string? name) => value;

        public IDisposable OnChange(Action<ScoringOptions, string?> listener) => new Nop();

        private sealed class Nop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed record Seeded(TestLibrary Library, Guid SessionId, Guid CandidateId, Guid FirstId, Guid SecondId);

    private static string AlternativesJson(Guid firstId, Guid secondId, string? firstCover, string? secondCover) =>
        $$"""
        [
          {"releaseId":"{{firstId}}","title":"Suddenly You","format":"other","year":2003,"score":0.78125,"creators":["Lisa Kleypas"],"coverUrl":{{Cover(firstCover)}},"alreadyOwnedCount":0},
          {"releaseId":"{{secondId}}","title":"Suddenly You","format":"other","year":2001,"score":0.78125,"creators":["Lisa Kleypas"],"coverUrl":{{Cover(secondCover)}},"alreadyOwnedCount":0}
        ]
        """;

    private static string Cover(string? url) => url is null ? "null" : $"\"{url}\"";

    private async Task<Seeded> SeedAsync(
        string? firstCover = "https://covers.test/2003.jpg",
        string? secondCover = "https://covers.test/2001.jpg",
        CandidateResolution resolution = CandidateResolution.Ambiguous,
        CandidateDecision decision = CandidateDecision.Undecided,
        bool needsArt = true)
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var sessionId = Guid.CreateVersion7();
        var candidateId = Guid.CreateVersion7();
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();

        await using var context = Db.CreateContext();
        context.ScanSessions.Add(new ScanSession
        {
            Id = sessionId,
            LibraryId = library.Id,
            UserId = library.ViewerId,
            Status = ScanSessionStatus.Open,
            CreatedAt = Now,
        });
        context.ScanCandidates.Add(new ScanCandidate
        {
            Id = candidateId,
            LibraryId = library.Id,
            SessionId = sessionId,
            Seq = 1,
            Source = CandidateSource.PictureText,
            ShotKind = ShotKind.SingleCover,
            VisibleText = "Suddenly You",
            Resolution = resolution,
            Tier = ScanTier.T2,
            Score = 0.78125,
            AlternativesJson = AlternativesJson(firstId, secondId, firstCover, secondCover),
            TraceJson = $$"""{"steps":[],"ownedAt":[],"needsArt":{{(needsArt ? "true" : "false")}} }""",
            Decision = decision,
            CreatedAt = Now,
        });
        await context.SaveChangesAsync();
        return new Seeded(library, sessionId, candidateId, firstId, secondId);
    }

    private static VerifyCoverArtwork.Handler Handler(
        AppDbContext context, ICurrentUser acting, ICoverFetcher covers, ICoverVerifier verifier, ICoverStore? coverStore = null) =>
        new(context, acting, new PostgresSessionLock(context), covers, coverStore ?? new StubCoverStore([]), verifier, new FixedScoring(new ScoringOptions()));

    private static async Task<JsonElement> LastStepOfAsync(PostgresFixture db, ICurrentUser acting, Guid candidateId)
    {
        await using var context = db.CreateContext(acting);
        var trace = await context.ScanCandidates.Where(c => c.Id == candidateId).Select(c => c.TraceJson).SingleAsync();
        using var parsed = JsonDocument.Parse(trace!);
        return parsed.RootElement.GetProperty("steps")[parsed.RootElement.GetProperty("steps").GetArrayLength() - 1].Clone();
    }

    private static async Task<JsonDocument> TraceOfAsync(PostgresFixture db, ICurrentUser acting, Guid candidateId)
    {
        await using var context = db.CreateContext(acting);
        var trace = await context.ScanCandidates.Where(c => c.Id == candidateId).Select(c => c.TraceJson).SingleAsync();
        return JsonDocument.Parse(trace!);
    }

    [Fact]
    public async Task A_clear_cover_match_reorders_and_preselects()
    {
        // The Suddenly You tie: identical text scores, but the capture is the 2001 cover.
        var seeded = await SeedAsync();
        var covers = new StubCovers(new Dictionary<string, byte[]>
        {
            ["https://covers.test/2003.jpg"] = [0x03],
            ["https://covers.test/2001.jpg"] = [0x01],
        });
        var verifier = new StubVerifier((_, cover) => cover[0] == 0x01 ? 120 : 4);
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);

        var result = await Handler(context, acting, covers, verifier).HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, JpegFixtures.Plain(), null), default);

        result.IsSuccess.ShouldBeTrue();
        var dto = result.Value;
        dto.Resolution.ShouldBe(CandidateResolution.Ambiguous);
        dto.Alternatives!.Select(a => a.Year!.Value).ShouldBe([2001, 2003]);
        dto.CoverPick.ShouldNotBeNull();
        dto.CoverPick.ReleaseId.ShouldBe(seeded.SecondId);
        dto.CoverPick.Reason.ShouldBe("cover_match");

        var last = await LastStepOfAsync(Db, acting, seeded.CandidateId);
        last.GetProperty("provider").GetString().ShouldBe("cover_verify");
        last.GetProperty("outcome").GetString().ShouldBe("preselected");
        using var trace = await TraceOfAsync(Db, acting, seeded.CandidateId);
        trace.RootElement.GetProperty("coverScores").GetArrayLength().ShouldBe(2);
        trace.RootElement.GetProperty("coverPick").GetProperty("releaseId").GetString().ShouldBe(seeded.SecondId.ToString());
    }

    [Fact]
    public async Task Close_visual_scores_rank_without_preselecting()
    {
        var seeded = await SeedAsync();
        var covers = new StubCovers(new Dictionary<string, byte[]>
        {
            ["https://covers.test/2003.jpg"] = [0x03],
            ["https://covers.test/2001.jpg"] = [0x01],
        });
        var verifier = new StubVerifier((_, cover) => cover[0] == 0x01 ? 85 : 55);
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);

        var result = await Handler(context, acting, covers, verifier).HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, JpegFixtures.Plain(), null), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Alternatives!.Select(a => a.Year!.Value).ShouldBe([2001, 2003]);
        result.Value.CoverPick.ShouldBeNull();

        var last = await LastStepOfAsync(Db, acting, seeded.CandidateId);
        last.GetProperty("outcome").GetString().ShouldBe("ranked");
    }

    [Fact]
    public async Task The_capture_quad_reaches_the_verifier()
    {
        var seeded = await SeedAsync();
        var covers = new StubCovers(new Dictionary<string, byte[]> { ["https://covers.test/2003.jpg"] = [0x03] });
        var verifier = new StubVerifier((_, _) => 120);
        var quad = new CoverQuad(new CoverPoint(0.1, 0.1), new CoverPoint(0.9, 0.1), new CoverPoint(0.9, 0.9), new CoverPoint(0.1, 0.9));
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);

        var result = await Handler(context, acting, covers, verifier).HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, JpegFixtures.Plain(), quad), default);

        result.IsSuccess.ShouldBeTrue();
        verifier.SeenQuad.ShouldBe(quad);
    }

    [Fact]
    public async Task A_degenerate_quad_is_refused_before_any_cover_is_fetched()
    {
        var seeded = await SeedAsync();
        var covers = new StubCovers(new Dictionary<string, byte[]>());
        var verifier = new StubVerifier((_, _) => 120);
        var quad = new CoverQuad(new CoverPoint(0, 0), new CoverPoint(0, 0), new CoverPoint(0, 0), new CoverPoint(0, 0));
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);

        var result = await Handler(context, acting, covers, verifier).HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, JpegFixtures.Plain(), quad), default);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("scan.cover_quad_invalid");
        covers.Fetched.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_settled_row_is_not_verified()
    {
        var seeded = await SeedAsync(resolution: CandidateResolution.Matched);
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);
        var handler = Handler(context, acting, new StubCovers([]), new StubVerifier((_, _) => 120));

        var result = await handler.HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, JpegFixtures.Plain(), null), default);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("scan.cover_not_needed");
    }

    [Fact]
    public async Task A_row_the_person_decided_is_left_alone()
    {
        var seeded = await SeedAsync(decision: CandidateDecision.Accepted);
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);
        var handler = Handler(context, acting, new StubCovers([]), new StubVerifier((_, _) => 120));

        var result = await handler.HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, JpegFixtures.Plain(), null), default);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("scan.art_candidate_decided");
    }

    [Fact]
    public async Task A_catalogue_cover_is_read_from_the_store_by_release_id()
    {
        var seeded = await SeedAsync(firstCover: null, secondCover: null);
        var assetId = Guid.CreateVersion7();
        await using (var seed = Db.CreateContext())
        {
            var workId = Guid.CreateVersion7();
            seed.Works.Add(new Work { Id = workId, Kind = WorkKind.Book, Title = "Suddenly You", CreatedAt = Now });
            seed.Releases.Add(new Release
            {
                Id = seeded.SecondId,
                WorkId = workId,
                Format = ReleaseFormat.Paperback,
                Source = ReleaseSource.User,
                CoverAssetId = assetId,
                CreatedAt = Now,
            });
            seed.Assets.Add(new Asset { Id = assetId, Kind = AssetKind.Cover, BlobPath = "ab/2001.png", ContentType = "image/png" });
            await seed.SaveChangesAsync();
        }

        var covers = new StubCovers(new Dictionary<string, byte[]>());
        var store = new StubCoverStore(new Dictionary<string, byte[]> { ["ab/2001.png"] = [0x01] });
        var verifier = new StubVerifier((_, cover) => cover[0] == 0x01 ? 150 : 4);
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);

        var result = await Handler(context, acting, covers, verifier, store).HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, JpegFixtures.Plain(), null), default);

        result.IsSuccess.ShouldBeTrue();
        covers.Fetched.ShouldBeEmpty();
        result.Value.Alternatives!.Select(a => a.Year!.Value).ShouldBe([2001, 2003]);
        result.Value.CoverPick!.ReleaseId.ShouldBe(seeded.SecondId);
    }

    [Fact]
    public async Task A_row_without_covers_is_refused()
    {
        var seeded = await SeedAsync(firstCover: null, secondCover: null);
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);
        var handler = Handler(context, acting, new StubCovers([]), new StubVerifier((_, _) => 120));

        var result = await handler.HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, JpegFixtures.Plain(), null), default);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("scan.cover_no_covers");
    }

    [Fact]
    public async Task Unreachable_covers_leave_the_row_for_later()
    {
        var seeded = await SeedAsync();
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);
        var handler = Handler(context, acting, new StubCovers([]), new StubVerifier((_, _) => 120));

        var result = await handler.HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, JpegFixtures.Plain(), null), default);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("scan.cover_unavailable");
    }

    [Fact]
    public async Task A_non_jpeg_capture_is_refused()
    {
        var seeded = await SeedAsync();
        var acting = seeded.Library.ActingAs(LibraryRole.Viewer);
        await using var context = Db.CreateContext(acting);
        var handler = Handler(context, acting, new StubCovers([]), new StubVerifier((_, _) => 120));

        var result = await handler.HandleAsync(
            new VerifyCoverArtwork.Command(seeded.SessionId, seeded.CandidateId, new byte[] { 1, 2, 3 }, null), default);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("scan.photo_not_jpeg");
    }
}
