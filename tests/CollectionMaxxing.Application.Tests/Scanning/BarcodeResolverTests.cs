using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>
/// The outside half of barcode resolution: what a product-barcode lookup becomes when the title-search providers can or
/// cannot run (#383: a game whose only answer is the UPCitemdb product must still add).
/// </summary>
public class BarcodeResolverTests
{
    /// <summary>The Hyrule Warriors: Age of Imprisonment barcode from #383: a valid GTIN-13 no book provider can answer.</summary>
    private const string HyruleCode = "0045496905934";

    private static Identifier HyruleId()
    {
        Identifier.TryParse(HyruleCode, out var id).ShouldBeTrue();
        return id!;
    }

    private static ProviderRelease HyruleProduct() => new()
    {
        Provider = "upcitemdb",
        ExternalId = HyruleCode,
        Title = "Hyrule Warriors: Age of Imprisonment",
        Kind = WorkKind.Game,
        Platform = "Switch 2",
        Publisher = "Nintendo",
        Identifiers = [HyruleId()],
    };

    /// <summary>A product lookup that answers one code, like UPCitemdb would.</summary>
    private sealed class StubProducts(Func<Identifier, ProductLookupResult> answer) : IProductLookup
    {
        public Task<ProductLookupResult> LookupAsync(Identifier id, CancellationToken cancellationToken) =>
            Task.FromResult(answer(id));
    }

    /// <summary>A lookup-only provider: Supports every kind but searches none, like UPCitemdb.</summary>
    private sealed class LookupOnlyProvider : ICatalogProvider
    {
        public string Name => "upcitemdb";

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
    }

    /// <summary>A title-search provider, like IGDB when it is configured.</summary>
    private sealed class SearchableProvider(IReadOnlyList<ProviderRelease> results) : ICatalogProvider, ISearchableCatalogProvider
    {
        public string Name => "igdb";

        public bool Supports(WorkKind kind) => kind == WorkKind.Game;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) =>
            Task.FromResult(results);
    }

    private sealed class EmptyIsbnStore : IIsbnStore
    {
        public Task<ProviderRelease?> FindAsync(string isbn13, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task SaveAsync(string isbn13, ProviderRelease release, CancellationToken ct) => Task.CompletedTask;
    }

    private static BarcodeResolver Resolver(IProductLookup products, params ICatalogProvider[] providers) => new(
        providers,
        new BookLookup([], new EmptyIsbnStore(), NullLogger<BookLookup>.Instance),
        products,
        new ScanningOptions(),
        NullLogger<BarcodeResolver>.Instance);

    [Fact]
    public async Task A_classified_product_with_no_searchable_provider_is_answered_directly()
    {
        var product = HyruleProduct();
        var resolver = Resolver(new StubProducts(_ => ProductLookupResult.Found(product)), new LookupOnlyProvider());

        var resolution = await resolver.ResolveAsync(HyruleId(), CancellationToken.None);

        resolution.Verdict.ShouldBe(ResolutionVerdict.Matched);
        resolution.ProductTitle.ShouldBe(product.Title);
        resolution.Options.ShouldHaveSingleItem();
        resolution.Options[0].Provider.ShouldBe(product);
        resolution.Options[0].Score.ShouldBe(BarcodeResolver.ProviderExactScore);
        resolution.Steps.Any(s => s is { Provider: "product-lookup", Outcome: "direct" }).ShouldBeTrue();
    }

    [Fact]
    public async Task A_search_that_ran_and_found_nothing_stays_not_found()
    {
        var product = HyruleProduct();
        var resolver = Resolver(
            new StubProducts(_ => ProductLookupResult.Found(product)), new LookupOnlyProvider(), new SearchableProvider([]));

        var resolution = await resolver.ResolveAsync(HyruleId(), CancellationToken.None);

        resolution.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        resolution.ProductTitle.ShouldBe(product.Title);
        resolution.Options.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_search_that_answers_is_scored_as_before()
    {
        var product = HyruleProduct();
        var game = product with { Provider = "igdb", ExternalId = "g1" };
        var resolver = Resolver(
            new StubProducts(_ => ProductLookupResult.Found(product)), new LookupOnlyProvider(), new SearchableProvider([game]));

        var resolution = await resolver.ResolveAsync(HyruleId(), CancellationToken.None);

        resolution.Verdict.ShouldBe(ResolutionVerdict.Matched);
        resolution.Options[0].Provider.ShouldBe(game);
        resolution.Steps.ShouldContain(s => s.Provider == "scorer");
    }

    [Fact]
    public async Task An_unclassified_product_is_never_answered_directly()
    {
        var product = HyruleProduct() with { Kind = WorkKind.Other, Platform = null };
        var resolver = Resolver(new StubProducts(_ => ProductLookupResult.Found(product)), new LookupOnlyProvider());

        var resolution = await resolver.ResolveAsync(HyruleId(), CancellationToken.None);

        resolution.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        resolution.Options.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_product_lookup_that_fails_stays_pending()
    {
        var resolver = Resolver(new StubProducts(_ => ProductLookupResult.Unavailable), new LookupOnlyProvider());

        var resolution = await resolver.ResolveAsync(HyruleId(), CancellationToken.None);

        resolution.Verdict.ShouldBe(ResolutionVerdict.Pending);
    }

    [Fact]
    public async Task A_product_nobody_knows_is_not_found()
    {
        var resolver = Resolver(new StubProducts(_ => ProductLookupResult.NotFound), new LookupOnlyProvider());

        var resolution = await resolver.ResolveAsync(HyruleId(), CancellationToken.None);

        resolution.Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }

    /// <summary>A valid ISBN-13 (an invented one) that only the book providers could answer.</summary>
    private const string BookCode = "9780000000002";

    private static Identifier BookId()
    {
        Identifier.TryParse(BookCode, out var id).ShouldBeTrue();
        return id!;
    }

    /// <summary>
    /// A provider that cannot be reached, the way the real adapters have one: it reports the failure and answers nothing,
    /// never throws. <paramref name="kind"/> is the one kind it knows.
    /// </summary>
    private sealed class DownProvider(string name, WorkKind kind) : ICatalogProvider, ISearchableCatalogProvider
    {
        public string Name => name;

        public bool Supports(WorkKind asked) => asked == kind;

        public async Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct)
        {
            await Task.Yield();
            ProviderCalls.ReportFailure();
            return null;
        }

        public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            await Task.Yield();
            ProviderCalls.ReportFailure();
            return [];
        }
    }

    private static BarcodeResolver BookResolver(IProductLookup products, params ICatalogProvider[] bookProviders) => new(
        bookProviders,
        new BookLookup(bookProviders, new EmptyIsbnStore(), NullLogger<BookLookup>.Instance),
        products,
        new ScanningOptions(),
        NullLogger<BarcodeResolver>.Instance);

    [Fact]
    public async Task A_title_search_whose_provider_could_not_be_reached_stays_pending()
    {
        var product = HyruleProduct();
        var resolver = Resolver(
            new StubProducts(_ => ProductLookupResult.Found(product)), new LookupOnlyProvider(), new DownProvider("igdb", WorkKind.Game));

        var resolution = await resolver.ResolveAsync(HyruleId(), CancellationToken.None);

        resolution.Verdict.ShouldBe(ResolutionVerdict.Pending);
        resolution.Steps.ShouldContain(s => s.Provider == "igdb" && s.Outcome == "failed");
    }

    [Fact]
    public async Task A_book_code_no_book_provider_could_reach_stays_pending()
    {
        var resolver = BookResolver(
            new StubProducts(_ => ProductLookupResult.NotFound), new DownProvider("openlibrary", WorkKind.Book), new DownProvider("googlebooks", WorkKind.Book));

        var resolution = await resolver.ResolveAsync(BookId(), CancellationToken.None);

        resolution.Verdict.ShouldBe(ResolutionVerdict.Pending);
        resolution.Steps.Where(s => s.Provider is "openlibrary" or "googlebooks").Select(s => s.Outcome).ShouldBe(["failed", "failed"]);
    }

    [Fact]
    public async Task A_book_code_the_book_providers_answered_but_do_not_know_is_not_found()
    {
        var resolver = BookResolver(new StubProducts(_ => ProductLookupResult.NotFound), new LookupOnlyBookProvider());

        var resolution = await resolver.ResolveAsync(BookId(), CancellationToken.None);

        resolution.Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }

    /// <summary>A book provider that is up and does not know the code.</summary>
    private sealed class LookupOnlyBookProvider : ICatalogProvider
    {
        public string Name => "openlibrary";

        public bool Supports(WorkKind kind) => kind == WorkKind.Book;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
    }
}
