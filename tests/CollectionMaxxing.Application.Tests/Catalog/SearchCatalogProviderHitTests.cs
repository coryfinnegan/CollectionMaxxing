using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace CollectionMaxxing.Application.Tests.Catalog;

/// <summary>A search keeps the provider hits it shows, so an add can name one; a result the catalogue already has needs no such name.</summary>
public class SearchCatalogProviderHitTests(PostgresFixture db) : DatabaseTest(db)
{
    private static ProviderRelease Hit(string externalId, string title) => new()
    {
        Provider = "igdb",
        ExternalId = externalId,
        WorkKey = externalId,
        Title = title,
        Kind = WorkKind.Game,
        Year = 1993,
        Creators = ["id Software"],
    };

    private SearchCatalog.Handler HandlerWith(IProviderHitStore? hits, params ProviderRelease[] answers) =>
        new(Db.CreateContext(), new NoLocalHits(), [new FixedProvider(answers)], new CatalogOptions(), NullLogger<SearchCatalog.Handler>.Instance, hits);

    private static SearchCatalog.Query Query(string q) => new(q, null, null, null, null);

    [Fact]
    public async Task Every_provider_hit_is_saved_and_carries_its_provider_and_id()
    {
        var hits = new RecordingHitStore();

        var result = await HandlerWith(hits, Hit("a", "Doom"), Hit("b", "Doom II")).HandleAsync(Query("doom"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var refs = result.Value.Items.SelectMany(r => r.Releases).Select(r => r.ProviderRef).ToList();
        refs.ShouldBe([new ProviderRefDto("igdb", "a"), new ProviderRefDto("igdb", "b")], ignoreOrder: true);
        hits.Saved.Select(s => s.ExternalId).Order().ShouldBe(["a", "b"]);
        hits.SaveCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_store_that_fails_never_fails_the_search()
    {
        var hits = new RecordingHitStore { Failure = new InvalidOperationException("db down") };

        var result = await HandlerWith(hits, Hit("a", "Doom")).HandleAsync(Query("doom"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Items.Single().Work.Title.ShouldBe("Doom");
    }

    [Fact]
    public async Task A_search_with_no_provider_hits_saves_nothing()
    {
        var hits = new RecordingHitStore();

        var result = await HandlerWith(hits).HandleAsync(Query("doom"), CancellationToken.None);

        result.Value.Items.ShouldBeEmpty();
        hits.SaveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_book_hit_keeps_its_barcodes_for_the_add()
    {
        var isbn = Identifier.TryParse("9780380817771", out var parsed) ? parsed : throw new InvalidOperationException("test ISBN");
        var hits = new RecordingHitStore();
        var book = new ProviderRelease { Provider = "igdb", ExternalId = "g1", Title = "Suddenly You", Kind = WorkKind.Book, Identifiers = [isbn] };

        await HandlerWith(hits, book).HandleAsync(Query("suddenly you"), CancellationToken.None);

        hits.Saved.Single().Identifiers.Single().Value.ShouldBe("9780380817771");
    }

    private sealed class RecordingHitStore : IProviderHitStore
    {
        public List<ProviderRelease> Saved { get; } = [];

        public int SaveCalls { get; private set; }

        public Exception? Failure { get; init; }

        public Task SaveAsync(IReadOnlyCollection<ProviderRelease> hits, CancellationToken ct)
        {
            SaveCalls++;
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            Saved.AddRange(hits);
            return Task.CompletedTask;
        }

        public Task<ProviderRelease?> FindAsync(string provider, string externalId, CancellationToken ct) =>
            Task.FromResult(Saved.FirstOrDefault(s => s.Provider == provider && s.ExternalId == externalId));
    }

    private sealed class NoLocalHits : ILocalCatalogSearch
    {
        public Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalHit>>([]);
    }

    private sealed class FixedProvider(IReadOnlyList<ProviderRelease> answers) : ICatalogProvider
    {
        public string Name => "igdb";

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) => Task.FromResult(answers);
    }
}
