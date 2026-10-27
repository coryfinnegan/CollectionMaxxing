using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Scanning;

/// <summary>
/// A game barcode when no title-search provider is configured (#383): the exact UPCitemdb product answers directly, so
/// the game resolves with its title instead of failing both barcode and cover, and waits for the Review commit (#452).
/// </summary>
public class BarcodeDirectAnswerTests(ApiFixture api) : ScanTest(api)
{
    /// <summary>The only provider: lookup-only like UPCitemdb, answering no title search.</summary>
    private sealed class LookupOnlyProvider : ICatalogProvider
    {
        public string Name => "upcitemdb";

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
    }

    [Fact]
    public async Task A_game_barcode_resolves_by_its_product_answer_without_any_title_search_provider()
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICatalogProvider>();
            services.AddSingleton<ICatalogProvider>(new LookupOnlyProvider());
        };
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(38301);
        await Catalog.CatalogSeed.AddPlatformAsync(Db, "Switch 2", "Switch 2");
        Products.Know(code, ScanData.Product("Hyrule Warriors: Age of Imprisonment", code, platform: "Switch 2", year: 2025));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);

        Resolution(candidates[0]).ShouldBe("matched");
        candidates[0].GetProperty("release").GetProperty("title").GetString().ShouldBe("Hyrule Warriors: Age of Imprisonment");
        candidates[0].GetProperty("release").GetProperty("platform").GetProperty("name").GetString().ShouldBe("Switch 2");
        Decision(candidates[0]).ShouldBe("accepted");
        (await CountItemsAsync(library.Id)).ShouldBe(0, "resolved at scan time, added only by the Review commit (#452)");
    }

    [Fact]
    public async Task An_unclassified_product_still_needs_its_cover_without_any_title_search_provider()
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICatalogProvider>();
            services.AddSingleton<ICatalogProvider>(new LookupOnlyProvider());
        };
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(38302);
        Products.Know(code, ScanData.Product("Obscure Gadget", code, platform: "Unknown") with { Kind = WorkKind.Other, Platform = null });
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await ScanAsync(editor, sessionId, code);

        Resolution(candidates[0]).ShouldBe("not_found");
        candidates[0].GetProperty("visibleText").GetString().ShouldBe("Obscure Gadget");
    }
}
