using System.Data.Common;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Events;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CollectionMaxxing.Application.Tests.Scanning.Events;

/// <summary>What each look at a scan session sends, and what it costs the database (docs/04, "Example: SSE event").</summary>
public class ScanEventFeedTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Trace = """{"steps":[],"ownedAt":["Shelf A"]}""";

    private const string Alternatives =
        """[{"releaseId":"6f1f2c9e-0d8e-4c64-9a52-3a3f7c1d5b10","title":"Wave Race 64","format":"game_cartridge","platform":null,"year":1996,"score":0.9,"creators":[],"coverUrl":null,"alreadyOwnedCount":0}]""";

    /// <summary>Keeps the text of every command the context runs.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Members(Guid libraryId) : IMembershipReader
    {
        public Task<IReadOnlyList<Membership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>([new Membership(libraryId, LibraryRole.Editor)]);
    }

    private async Task<(TestLibrary Library, Guid SessionId)> ArrangeAsync()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        return (library, await TestData.CreateScanSessionAsync(Db, library));
    }

    private async Task<ScanCandidate> AddCandidateAsync(
        TestLibrary library,
        Guid sessionId,
        int seq,
        int alreadyOwnedCount = 0,
        CandidateDecision decision = CandidateDecision.Undecided,
        Guid? createdItemId = null,
        Guid? snapId = null)
    {
        var candidate = new ScanCandidate
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            SessionId = sessionId,
            Seq = seq,
            Source = CandidateSource.Barcode,
            RawCode = $"0{seq}",
            Resolution = CandidateResolution.Matched,
            Decision = decision,
            AlreadyOwnedCount = alreadyOwnedCount,
            CreatedItemId = createdItemId,
            SnapId = snapId,
            InferredJson = """{"completeness":"complete"}""",
            AlternativesJson = Alternatives,
            TraceJson = Trace,
            CreatedAt = Now,
        };
        await using var context = Db.CreateContext();
        context.ScanCandidates.Add(candidate);
        await context.SaveChangesAsync();
        return candidate;
    }

    private async Task UpdateCandidateAsync(TestLibrary library, Guid candidateId, int copies = 1, CandidateDecision? decision = null)
    {
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        await context.ScanCandidates.Where(c => c.Id == candidateId).ExecuteUpdateAsync(
            set => set.SetProperty(c => c.Copies, copies).SetProperty(c => c.Decision, decision ?? CandidateDecision.Undecided));
    }

    private static async Task<ScanEventCursor> OpenAsync(AppDbContext context, TestLibrary library, Guid sessionId, int? lastEventId = null)
    {
        var feed = new ScanEventFeed(context, library.ActingAs(LibraryRole.Editor), new Members(library.Id));
        return (await feed.OpenAsync(sessionId, lastEventId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    private static async Task<IReadOnlyList<ScanEvent>> LookAsync(ScanEventCursor cursor) =>
        (await cursor.NextAsync(TestContext.Current.CancellationToken)).Events;

    [Fact]
    public async Task An_idle_look_runs_one_small_query_and_loads_no_candidates()
    {
        var (library, sessionId) = await ArrangeAsync();
        for (var seq = 1; seq <= 3; seq++)
        {
            await AddCandidateAsync(library, sessionId, seq);
        }

        var log = new CommandLog();
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor), log);
        var cursor = await OpenAsync(context, library, sessionId);
        (await LookAsync(cursor)).ShouldNotBeEmpty();
        log.Commands.Clear();

        var idle = await cursor.NextAsync(TestContext.Current.CancellationToken);

        idle.Events.ShouldBeEmpty();
        idle.SessionEnded.ShouldBeFalse();
        var query = log.Commands.ShouldHaveSingleItem();
        query.ShouldContain("change_seq");
        foreach (var payload in new[] { "alternatives", "trace", "inferred" })
        {
            query.ShouldNotContain(payload);
        }
    }

    [Fact]
    public async Task A_session_that_ends_without_any_candidate_changing_closes_the_stream()
    {
        var (library, sessionId) = await ArrangeAsync();
        await AddCandidateAsync(library, sessionId, 1);
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var cursor = await OpenAsync(context, library, sessionId);
        await LookAsync(cursor);
        await using (var other = Db.CreateContext(library.ActingAs(LibraryRole.Editor)))
        {
            await other.ScanSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(
                set => set.SetProperty(s => s.Status, ScanSessionStatus.Abandoned), TestContext.Current.CancellationToken);
        }

        var ended = await cursor.NextAsync(TestContext.Current.CancellationToken);

        ended.SessionEnded.ShouldBeTrue();
        var status = ended.Events.ShouldHaveSingleItem();
        status.Name.ShouldBe(ScanEventNames.SessionStatus);
        ((SessionStatusDto)status.Data).Status.ShouldBe(ScanSessionStatus.Abandoned);
    }

    [Fact]
    public async Task A_changed_candidate_is_delivered_once_with_its_whole_payload()
    {
        var (library, sessionId) = await ArrangeAsync();
        var first = await AddCandidateAsync(library, sessionId, 1);
        await AddCandidateAsync(library, sessionId, 2);
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var cursor = await OpenAsync(context, library, sessionId);
        await LookAsync(cursor);

        await UpdateCandidateAsync(library, first.Id, copies: 3);
        var changed = await LookAsync(cursor);

        var update = changed.ShouldHaveSingleItem();
        update.Name.ShouldBe(ScanEventNames.CandidateUpdated);
        var dto = (CandidateDto)update.Data;
        dto.Id.ShouldBe(first.Id);
        dto.Copies.ShouldBe(3);
        update.Id.ShouldBe(dto.ChangeSeq);
        dto.ChangeSeq.ShouldBeGreaterThan(2);
        dto.OwnedAt.ShouldBe(["Shelf A"]);
        dto.Alternatives.ShouldNotBeNull().ShouldHaveSingleItem().Title.ShouldBe("Wave Race 64");
        dto.Inferred.ShouldNotBeNull().GetProperty("completeness").GetString().ShouldBe("complete");
        (await LookAsync(cursor)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_stream_adds_every_candidate_in_the_order_they_changed()
    {
        var (library, sessionId) = await ArrangeAsync();
        var first = await AddCandidateAsync(library, sessionId, 1);
        var second = await AddCandidateAsync(library, sessionId, 2);
        var third = await AddCandidateAsync(library, sessionId, 3);
        await UpdateCandidateAsync(library, first.Id, copies: 2); // the first now changed last
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));

        var events = await LookAsync(await OpenAsync(context, library, sessionId));

        var candidates = events.Where(e => e.Name.StartsWith("candidate.", StringComparison.Ordinal)).ToList();
        candidates.Select(e => e.Name).ShouldAllBe(name => name == ScanEventNames.CandidateAdded);
        candidates.Select(e => ((CandidateDto)e.Data).Id).ShouldBe([second.Id, third.Id, first.Id]);
        candidates.Select(e => e.Id).ShouldBe(candidates.Select(e => e.Id).Order());
    }

    [Fact]
    public async Task A_reconnecting_client_gets_what_changed_since_its_last_event_id()
    {
        var (library, sessionId) = await ArrangeAsync();
        var first = await AddCandidateAsync(library, sessionId, 1);
        var second = await AddCandidateAsync(library, sessionId, 2);
        var third = await AddCandidateAsync(library, sessionId, 3);
        await UpdateCandidateAsync(library, first.Id, copies: 2);
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));

        // It had been shown the first two (as they were then), then went away.
        var events = await LookAsync(await OpenAsync(context, library, sessionId, lastEventId: second.ChangeSeq));

        var candidates = events.Where(e => e.Name.StartsWith("candidate.", StringComparison.Ordinal)).ToList();
        candidates.Select(e => (e.Name, ((CandidateDto)e.Data).Id)).ShouldBe(
        [
            (ScanEventNames.CandidateAdded, third.Id),
            (ScanEventNames.CandidateUpdated, first.Id),
        ]);
    }

    [Fact]
    public async Task A_candidate_the_client_had_before_it_reconnected_is_updated_when_it_changes_later()
    {
        var (library, sessionId) = await ArrangeAsync();
        var first = await AddCandidateAsync(library, sessionId, 1);
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var cursor = await OpenAsync(context, library, sessionId, lastEventId: first.ChangeSeq);
        (await LookAsync(cursor)).Select(e => e.Name).ShouldNotContain(ScanEventNames.CandidateAdded);

        await UpdateCandidateAsync(library, first.Id, copies: 2);

        var update = (await LookAsync(cursor)).ShouldHaveSingleItem();
        update.Name.ShouldBe(ScanEventNames.CandidateUpdated);
    }

    [Fact]
    public async Task Counters_match_a_direct_count_of_the_session()
    {
        var (library, sessionId) = await ArrangeAsync();
        var itemId = await TestData.CreateItemAsync(Db, library);
        await AddCandidateAsync(library, sessionId, 1, createdItemId: itemId);
        await AddCandidateAsync(library, sessionId, 2, alreadyOwnedCount: 1, decision: CandidateDecision.Rejected);
        await AddCandidateAsync(library, sessionId, 3, decision: CandidateDecision.Rejected);
        await AddCandidateAsync(library, sessionId, 4);
        await AddCandidateAsync(library, sessionId, 5);
        await using (var seed = Db.CreateContext())
        {
            seed.ProvisionalRecords.AddRange(
                Provisional(library, sessionId, resolved: false),
                Provisional(library, sessionId, resolved: true));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var events = await LookAsync(await OpenAsync(context, library, sessionId));

        var sent = events.Single(e => e.Name == ScanEventNames.SessionCounters).Data.ShouldBeOfType<SessionCountersEventDto>();
        var candidates = await context.ScanCandidates.AsNoTracking().Where(c => c.SessionId == sessionId).ToListAsync(TestContext.Current.CancellationToken);
        sent.Counters.ShouldBe(SessionCounters.Count(candidates, openProvisionals: 1));
        sent.Counters.ShouldBe(new SessionCountersDto(Captured: 6, Added: 1, ToReview: 3, Owned: 1, Skipped: 1));
        sent.ChangeSeq.ShouldBe(candidates.Max(c => c.ChangeSeq));
    }

    [Fact]
    public async Task Counters_are_sent_again_when_a_candidate_changes_bucket_and_not_otherwise()
    {
        var (library, sessionId) = await ArrangeAsync();
        var candidate = await AddCandidateAsync(library, sessionId, 1);
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var cursor = await OpenAsync(context, library, sessionId);
        await LookAsync(cursor);

        await UpdateCandidateAsync(library, candidate.Id, copies: 2);
        (await LookAsync(cursor)).Select(e => e.Name).ShouldNotContain(ScanEventNames.SessionCounters);

        await UpdateCandidateAsync(library, candidate.Id, decision: CandidateDecision.Rejected);
        var counters = (await LookAsync(cursor)).Single(e => e.Name == ScanEventNames.SessionCounters);
        ((SessionCountersEventDto)counters.Data).Counters.ShouldBe(new SessionCountersDto(Captured: 1, Added: 0, ToReview: 0, Owned: 0, Skipped: 1));
    }

    [Fact]
    public async Task A_photo_is_announced_once_per_connection_even_when_its_candidate_is_unchanged()
    {
        var (library, sessionId) = await ArrangeAsync();
        var snapId = await AddPhotoSnapAsync(library, sessionId);
        var candidate = await AddCandidateAsync(library, sessionId, 1, snapId: snapId);
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var cursor = await OpenAsync(context, library, sessionId, lastEventId: candidate.ChangeSeq);

        var connected = await LookAsync(cursor);

        connected.Select(e => e.Name).ShouldNotContain(ScanEventNames.CandidateAdded);
        var photo = connected.Single(e => e.Name == ScanEventNames.PhotoStatus);
        photo.Data.ShouldBe(new PhotoStatusDto(candidate.Id, PhotoStatusDto.Ready));
        (await LookAsync(cursor)).ShouldBeEmpty();
    }

    private static ProvisionalRecord Provisional(TestLibrary library, Guid sessionId, bool resolved) => new()
    {
        Id = Guid.CreateVersion7(),
        LibraryId = library.Id,
        SessionId = sessionId,
        TitleGuess = "Wave Race",
        MediaHint = ScanMediaHint.Games,
        CreatedAt = Now,
        ResolvedAt = resolved ? Now : null,
        ResolveAction = resolved ? ProvisionalResolveAction.ResolvedToExisting : null,
    };

    private async Task<Guid> AddPhotoSnapAsync(TestLibrary library, Guid sessionId)
    {
        var asset = new Asset
        {
            Id = Guid.CreateVersion7(),
            Kind = AssetKind.ScanPhoto,
            BlobPath = "scan/photo.jpg",
            ContentType = "image/jpeg",
            Sha256 = new string('a', 64),
            OwnerLibraryId = library.Id,
            CreatedAt = Now,
        };
        var snap = new ScanSnap
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            SessionId = sessionId,
            Trigger = SnapTrigger.Shutter,
            Engine = ScanEngine.VisionPipeline,
            ItemCount = 1,
            AssetId = asset.Id,
            CreatedAt = Now,
        };
        await using var context = Db.CreateContext();
        context.Assets.Add(asset);
        context.ScanSnaps.Add(snap);
        await context.SaveChangesAsync();
        return snap.Id;
    }
}
