using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Ownership;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Tests.Scanning.Learning;

/// <summary>
/// A barcode a person picked by hand is that library's own knowledge (ADR-027, a hosted multi-user service): another
/// library must never be answered from it, even when the pick was wrong or hostile. Links that come from a provider or
/// the catalogue are the same for everyone.
/// </summary>
public class LearnedBarcodeScopeTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string Code = "9780441172719";

    private static Identifier Barcode(string code = Code) =>
        Identifier.TryParse(code, out var id) ? id : throw new InvalidOperationException(code);

    private static CandidateResolver ResolverFor(AppDbContext context) => new(
        context,
        new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System),
        new OwnershipQuery(context),
        TimeProvider.System,
        new FixedScoring());

    private sealed class FixedScoring : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue { get; } = new();

        public ScoringOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ScoringOptions, string?> listener) => null;
    }

    /// <summary>The library confirms <paramref name="code"/> for <paramref name="releaseId"/> in a scan session, as a commit does.</summary>
    private async Task TeachAsync(TestLibrary library, Guid releaseId, string code = Code)
    {
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var session = await context.ScanSessions.SingleAsync(s => s.Id == sessionId);
        var candidate = new ScanCandidate
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            SessionId = sessionId,
            Seq = 1,
            Source = CandidateSource.Barcode,
            RawCode = code,
            MatchedReleaseId = releaseId,
            Resolution = CandidateResolution.Matched,
            Decision = CandidateDecision.Accepted,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        await new BarcodeLearner(context).LearnAsync(session, [candidate], TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task GiveProviderBarcodeAsync(Guid releaseId, string code = Code)
    {
        await using var context = Db.CreateContext();
        var id = Barcode(code);
        context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = releaseId, Type = id.Type, Value = id.Value });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<Guid?>> LinkLibrariesAsync(Guid releaseId)
    {
        await using var context = Db.CreateContext();
        return await context.ReleaseIdentifiers.AsNoTracking().Where(i => i.ReleaseId == releaseId).Select(i => i.LibraryId).ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<Guid>?> LocalHitAsync(Guid libraryId, string code = Code)
    {
        await using var context = Db.CreateContext();
        var found = await ResolverFor(context).FindLocalAsync(libraryId, [Barcode(code)], TestContext.Current.CancellationToken);
        return found.TryGetValue(Barcode(code).Value, out var releases) ? releases : null;
    }

    [Fact]
    public async Task A_barcode_a_library_taught_is_a_local_hit_for_that_library()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);
        await TeachAsync(mine, release);

        (await LocalHitAsync(mine.Id)).ShouldBe([release]);
        (await LinkLibrariesAsync(release)).ShouldBe([mine.Id]);
    }

    [Fact]
    public async Task A_barcode_a_library_taught_is_not_a_local_hit_for_another_library()
    {
        var teacher = await TestData.CreateLibraryAsync(Db);
        var other = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);
        await TeachAsync(teacher, release);

        (await LocalHitAsync(other.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_barcode_a_provider_gave_a_release_is_a_local_hit_for_every_library()
    {
        var first = await TestData.CreateLibraryAsync(Db);
        var second = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);
        await GiveProviderBarcodeAsync(release);

        (await LocalHitAsync(first.Id)).ShouldBe([release]);
        (await LocalHitAsync(second.Id)).ShouldBe([release]);
    }

    [Fact]
    public async Task Two_libraries_can_each_teach_the_same_barcode_for_the_same_release_and_neither_lends_it_to_a_third()
    {
        var first = await TestData.CreateLibraryAsync(Db);
        var second = await TestData.CreateLibraryAsync(Db);
        var third = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);
        await TeachAsync(first, release);
        await TeachAsync(second, release);

        (await LinkLibrariesAsync(release)).ShouldBe([first.Id, second.Id], ignoreOrder: true);
        (await LocalHitAsync(first.Id)).ShouldBe([release]);
        (await LocalHitAsync(second.Id)).ShouldBe([release]);
        (await LocalHitAsync(third.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_barcode_the_release_already_has_for_everyone_is_not_taught_again()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);
        await GiveProviderBarcodeAsync(release);

        await TeachAsync(library, release);

        (await LinkLibrariesAsync(release)).ShouldBe([null]);
    }

    [Fact]
    public async Task Teaching_the_same_barcode_again_keeps_one_link_for_the_library()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);

        await TeachAsync(library, release);
        await TeachAsync(library, release);

        (await LinkLibrariesAsync(release)).ShouldBe([library.Id]);
    }

    [Fact]
    public async Task A_barcode_confirmed_outside_a_scan_session_is_private_to_the_library_too()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var other = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);

        await using (var context = Db.CreateContext(mine.ActingAs(LibraryRole.Editor)))
        {
            await new BarcodeLearner(context).LearnAsync(mine.Id, release, Barcode(), TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await LocalHitAsync(mine.Id)).ShouldBe([release]);
        (await LocalHitAsync(other.Id)).ShouldBeNull();
        (await LinkLibrariesAsync(release)).ShouldBe([mine.Id]);
    }

    [Fact]
    public async Task A_provider_answer_does_not_reuse_a_release_through_another_librarys_learned_barcode()
    {
        var teacher = await TestData.CreateLibraryAsync(Db);
        var other = await TestData.CreateLibraryAsync(Db);
        var taught = await TestData.CreateReleaseAsync(Db, title: "Wrongly Picked Film");
        await TeachAsync(teacher, taught);

        await using var context = Db.CreateContext();
        var forOther = await new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System)
            .EnsureAsync(Book("A Completely Different Book"), other.Id, TestContext.Current.CancellationToken);

        forOther.IsNew.ShouldBeTrue();
        forOther.Id.ShouldNotBe(taught);
    }

    [Fact]
    public async Task A_provider_answer_reuses_a_release_through_the_libraries_own_learned_barcode()
    {
        var teacher = await TestData.CreateLibraryAsync(Db);
        var taught = await TestData.CreateReleaseAsync(Db, title: "Wrongly Picked Film");
        await TeachAsync(teacher, taught);

        await using var context = Db.CreateContext();
        var forTeacher = await new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System)
            .EnsureAsync(Book("A Completely Different Book"), teacher.Id, TestContext.Current.CancellationToken);

        forTeacher.IsNew.ShouldBeFalse();
        forTeacher.Id.ShouldBe(taught);
    }

    [Fact]
    public async Task A_provider_answer_reuses_a_release_through_a_barcode_everyone_shares()
    {
        var first = await TestData.CreateLibraryAsync(Db);
        var second = await TestData.CreateLibraryAsync(Db);
        var shared = await TestData.CreateReleaseAsync(Db, title: "Shared Film");
        await GiveProviderBarcodeAsync(shared);

        await using var context = Db.CreateContext();
        var store = new ReleaseStore(context, new FakeIdGenerator(), TimeProvider.System);

        (await store.EnsureAsync(Book("Whatever The Title Is"), first.Id, TestContext.Current.CancellationToken)).Id.ShouldBe(shared);
        (await store.EnsureAsync(Book("Whatever The Title Is"), second.Id, TestContext.Current.CancellationToken)).Id.ShouldBe(shared);
    }

    [Fact]
    public async Task A_barcode_another_library_taught_does_not_make_this_library_own_that_release()
    {
        var teacher = await TestData.CreateLibraryAsync(Db);
        var owner = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);
        await TestData.CreateItemAsync(Db, owner, release);
        await TeachAsync(teacher, release);

        await using var context = Db.CreateContext(owner.ActingAs(LibraryRole.Viewer));
        var answer = await new OwnershipQuery(context).ForCodeAsync(owner.Id, Barcode(), TestContext.Current.CancellationToken);

        answer.Owned.ShouldBeFalse();
    }

    [Fact]
    public async Task A_barcode_the_library_taught_makes_it_own_that_release()
    {
        var owner = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);
        await TestData.CreateItemAsync(Db, owner, release);
        await TeachAsync(owner, release);

        await using var context = Db.CreateContext(owner.ActingAs(LibraryRole.Viewer));
        var answer = await new OwnershipQuery(context).ForCodeAsync(owner.Id, Barcode(), TestContext.Current.CancellationToken);

        answer.Owned.ShouldBeTrue();
    }

    private static ProviderRelease Book(string title) => new()
    {
        Provider = "openlibrary",
        ExternalId = "OL1M",
        Title = title,
        Kind = WorkKind.Book,
        Format = ReleaseFormat.Paperback,
        Year = 1965,
        Identifiers = [Barcode()],
    };
}
