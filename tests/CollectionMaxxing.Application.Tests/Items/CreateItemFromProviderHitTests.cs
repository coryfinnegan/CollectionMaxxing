using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Tests.Items;

/// <summary>Adding an item by naming a provider hit the search showed: the work and release are saved the way a scan saves them.</summary>
public class CreateItemFromProviderHitTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly SteppingClock Clock = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));

    private static ProviderRelease Game(string externalId = "mario-n64", string title = "Super Mario 64") => new()
    {
        Provider = "igdb",
        ExternalId = externalId,
        WorkKey = "1074",
        Title = title,
        Kind = WorkKind.Game,
        Platform = "Nintendo 64",
        Year = 1996,
        Creators = ["Nintendo EAD"],
    };

    private static CreateItem.Command Add(Guid libraryId, string key, string? provider, string? externalId, Guid? releaseId = null, ManualRelease? manual = null) =>
        new(libraryId, key, releaseId, manual, null, null, null, null, null, null, null, null,
            provider is null && externalId is null ? null : new ProviderRefDto(provider!, externalId!));

    private Task<Result<Versioned<ItemDto>>> RunAsync(TestLibrary library, CreateItem.Command command, IProviderHitStore? hits)
    {
        var caller = library.ActingAs(LibraryRole.Editor);
        var context = Db.CreateContext(caller);
        var ids = new FakeIdGenerator();
        var handler = new CreateItem.Handler(context, ids, caller, Clock, releases: new ReleaseStore(context, ids, Clock), hits: hits);
        return handler.HandleAsync(command, CancellationToken.None);
    }

    [Fact]
    public async Task A_hit_the_search_showed_is_saved_as_a_catalogue_release_and_the_item_points_at_it()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var hits = new FakeHitStore(Game());

        var result = await RunAsync(library, Add(library.Id, "k1", "igdb", "mario-n64"), hits);

        result.IsSuccess.ShouldBeTrue(result.Error?.Code);
        await using var check = Db.CreateContext(library.ActingAs(LibraryRole.Viewer));
        var release = await check.Releases.AsNoTracking().SingleAsync();
        release.Id.ShouldBe(result.Value.Value.ReleaseId);
        release.OwnerLibraryId.ShouldBeNull();
        release.Source.ShouldBe(ReleaseSource.Provider);
        (await check.Works.AsNoTracking().SingleAsync()).Title.ShouldBe("Super Mario 64");
    }

    [Fact]
    public async Task A_hit_whose_product_the_catalogue_already_has_reuses_that_release_instead_of_adding_a_work()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var hits = new FakeHitStore(Game());
        var first = await RunAsync(library, Add(library.Id, "k1", "igdb", "mario-n64"), hits);

        // The same game under another provider's id and a differently spelt title: same work, same format, same platform.
        hits.Add(Game("mario-64-other", "super mario 64") with { Provider = "tmdb" });
        var second = await RunAsync(library, Add(library.Id, "k2", "tmdb", "mario-64-other"), hits);

        second.Value.Value.ReleaseId.ShouldBe(first.Value.Value.ReleaseId);
        await using var check = Db.CreateContext(library.ActingAs(LibraryRole.Viewer));
        (await check.Works.AsNoTracking().CountAsync()).ShouldBe(1);
        (await check.Releases.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_hit_nobody_showed_is_not_found_and_saves_nothing()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        var result = await RunAsync(library, Add(library.Id, "k1", "igdb", "never-shown"), new FakeHitStore(Game()));

        result.Error!.Code.ShouldBe(ItemErrors.ProviderHitNotFoundCode);
        result.Error.Kind.ShouldBe(ErrorKind.NotFound);
        await using var check = Db.CreateContext(library.ActingAs(LibraryRole.Viewer));
        (await check.Works.AsNoTracking().CountAsync()).ShouldBe(0);
        (await check.Items.AsNoTracking().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Without_a_hit_store_a_provider_ref_is_not_found_rather_than_a_crash()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        var result = await RunAsync(library, Add(library.Id, "k1", "igdb", "mario-n64"), hits: null);

        result.Error!.Code.ShouldBe(ItemErrors.ProviderHitNotFoundCode);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("igdb", "")]
    [InlineData("  ", "  ")]
    public async Task A_provider_ref_needs_both_its_provider_and_its_id(string provider, string externalId)
    {
        var library = await TestData.CreateLibraryAsync(Db);

        var result = await RunAsync(library, Add(library.Id, "k1", provider, externalId), new FakeHitStore(Game()));

        result.Error!.Code.ShouldBe("item.provider_ref_invalid");
    }

    [Fact]
    public async Task Exactly_one_of_release_provider_ref_and_manual_details_is_needed()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var hits = new FakeHitStore(Game());

        var neither = await RunAsync(library, Add(library.Id, "k1", null, null), hits);
        var releaseAndRef = await RunAsync(library, Add(library.Id, "k2", "igdb", "mario-n64", releaseId: Guid.CreateVersion7()), hits);
        var manualAndRef = await RunAsync(
            library, Add(library.Id, "k3", "igdb", "mario-n64", manual: new ManualRelease(WorkKind.Game, "Doom", null, null, null, null, null)), hits);

        neither.Error!.Code.ShouldBe("item.source_required");
        releaseAndRef.Error!.Code.ShouldBe("item.source_required");
        manualAndRef.Error!.Code.ShouldBe("item.source_required");
    }

    [Fact]
    public async Task Replaying_the_same_key_after_the_hit_expired_still_returns_the_item()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var hits = new FakeHitStore(Game());
        var first = await RunAsync(library, Add(library.Id, "same-key", "igdb", "mario-n64"), hits);

        hits.Clear(); // the day has passed
        var replay = await RunAsync(library, Add(library.Id, "same-key", "igdb", "mario-n64"), hits);

        replay.IsSuccess.ShouldBeTrue(replay.Error?.Code);
        replay.Value.Value.Id.ShouldBe(first.Value.Value.Id);
    }

    [Fact]
    public async Task The_same_key_for_a_different_hit_is_refused()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var hits = new FakeHitStore(Game(), Game("doom", "Doom"));
        await RunAsync(library, Add(library.Id, "same-key", "igdb", "mario-n64"), hits);

        var different = await RunAsync(library, Add(library.Id, "same-key", "igdb", "doom"), hits);

        different.Error!.Code.ShouldBe(ItemErrors.KeyReusedCode);
    }

    [Fact]
    public async Task A_hit_with_a_barcode_the_catalogue_knows_joins_that_release()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var knownReleaseId = await SeedBookWithIsbnAsync("Suddenly You", "9780380817771");
        var book = new ProviderRelease
        {
            Provider = "googlebooks",
            ExternalId = "g1",
            Title = "Suddenly You",
            Kind = WorkKind.Book,
            Year = 2001,
            Creators = ["Lisa Kleypas"],
            Identifiers = [Identifier.TryParse("9780380817771", out var isbn) ? isbn : throw new InvalidOperationException("test ISBN")],
        };

        var result = await RunAsync(library, Add(library.Id, "k1", "googlebooks", "g1"), new FakeHitStore(book));

        result.Value.Value.ReleaseId.ShouldBe(knownReleaseId);
        await using var check = Db.CreateContext(library.ActingAs(LibraryRole.Viewer));
        (await check.Works.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    private async Task<Guid> SeedBookWithIsbnAsync(string title, string isbn)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var work = new Work
        {
            Id = Guid.CreateVersion7(),
            Kind = WorkKind.Book,
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            NormalizedTitle = title.ToLowerInvariant(),
            OriginalYear = 2001,
            CreatorsJson = """["Lisa Kleypas"]""",
            CreatedAt = now,
        };
        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = work.Id,
            Format = ReleaseFormat.Paperback,
            Source = ReleaseSource.Provider,
            CreatedAt = now,
        };
        await using var context = Db.CreateContext();
        context.Works.Add(work);
        context.Releases.Add(release);
        context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = release.Id, Type = IdentifierType.Isbn13, Value = isbn });
        await context.SaveChangesAsync();
        return release.Id;
    }

    private sealed class FakeHitStore : IProviderHitStore
    {
        private readonly Dictionary<(string, string), ProviderRelease> _hits = [];

        public FakeHitStore(params ProviderRelease[] hits)
        {
            foreach (var hit in hits)
            {
                Add(hit);
            }
        }

        public void Add(ProviderRelease hit) => _hits[(hit.Provider, hit.ExternalId)] = hit;

        public void Clear() => _hits.Clear();

        public Task SaveAsync(IReadOnlyCollection<ProviderRelease> hits, CancellationToken ct)
        {
            foreach (var hit in hits)
            {
                Add(hit);
            }

            return Task.CompletedTask;
        }

        public Task<ProviderRelease?> FindAsync(string provider, string externalId, CancellationToken ct) =>
            Task.FromResult(_hits.GetValueOrDefault((provider, externalId)));
    }
}
