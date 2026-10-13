using System.Reflection;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Application.Tests.Catalog.Providers;

// Locked acceptance tests for CAT-1: the provider port is exactly the one in docs/02 "Providers". Do not weaken, skip or
// delete. (The doc's MediaKind is the existing WorkKind; see the PR notes.)
public class CatalogProviderPortTests
{
    [Fact]
    public void The_port_has_exactly_the_four_members_of_docs_02()
    {
        var members = typeof(ICatalogProvider)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToArray();

        members.ShouldBe(["LookupAsync", "Name", "SearchAsync", "Supports", "get_Name"], ignoreOrder: true);
    }

    [Fact]
    public void The_port_signatures_match_docs_02()
    {
        var t = typeof(ICatalogProvider);

        t.GetProperty(nameof(ICatalogProvider.Name))!.PropertyType.ShouldBe(typeof(string));

        var supports = t.GetMethod(nameof(ICatalogProvider.Supports))!;
        supports.ReturnType.ShouldBe(typeof(bool));
        supports.GetParameters().Select(p => p.ParameterType).ShouldBe([typeof(WorkKind)]);

        var lookup = t.GetMethod(nameof(ICatalogProvider.LookupAsync))!;
        lookup.ReturnType.ShouldBe(typeof(Task<ProviderRelease?>));
        lookup.GetParameters().Select(p => p.ParameterType).ShouldBe([typeof(Identifier), typeof(CancellationToken)]);

        var search = t.GetMethod(nameof(ICatalogProvider.SearchAsync))!;
        search.ReturnType.ShouldBe(typeof(Task<IReadOnlyList<ProviderRelease>>));
        search.GetParameters().Select(p => p.ParameterType).ShouldBe([typeof(CatalogQuery), typeof(CancellationToken)]);
    }

    [Fact]
    public async Task A_provider_can_be_implemented_against_the_port()
    {
        var id = new Identifier(IdentifierType.Isbn13, "9780306406157");
        var release = new ProviderRelease { Provider = "fake", ExternalId = "1", Title = "A Book", Kind = WorkKind.Book };
        ICatalogProvider provider = new FakeProvider(release);

        provider.Name.ShouldBe("fake");
        (await provider.LookupAsync(id, CancellationToken.None)).ShouldBe(release);
        (await provider.SearchAsync(new CatalogQuery { Title = "A Book" }, CancellationToken.None)).ShouldHaveSingleItem();
        provider.Supports(WorkKind.Book).ShouldBeTrue();
        provider.Supports(WorkKind.Game).ShouldBeFalse();
    }

    private sealed class FakeProvider(ProviderRelease release) : ICatalogProvider
    {
        public string Name => "fake";

        public bool Supports(WorkKind kind) => kind == WorkKind.Book;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) =>
            Task.FromResult<ProviderRelease?>(release);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderRelease>>([release]);
    }

    [Fact]
    public void A_query_defaults_to_ten_results_and_needs_only_a_title()
    {
        var query = new CatalogQuery { Title = "Alien" };

        query.Limit.ShouldBe(10);
        query.Kind.ShouldBeNull();
        query.Platform.ShouldBeNull();
        query.Year.ShouldBeNull();
    }

    [Fact]
    public void A_provider_release_maps_to_a_scoring_candidate_keyed_by_provider_and_id()
    {
        var id = new Identifier(IdentifierType.Gtin13, "4006381333931");
        var release = new ProviderRelease
        {
            Provider = "igdb",
            ExternalId = "1074",
            Title = "Super Mario 64",
            Kind = WorkKind.Game,
            Format = ReleaseFormat.GameCartridge,
            Platform = "Nintendo 64",
            Year = 1996,
            Creators = ["Nintendo EAD"],
            Edition = "Player's Choice",
            Identifiers = [id],
        };

        var candidate = release.ToCandidate();

        candidate.Key.ShouldBe("igdb:1074");
        candidate.Title.ShouldBe("Super Mario 64");
        candidate.Kind.ShouldBe(WorkKind.Game);
        candidate.Format.ShouldBe(ReleaseFormat.GameCartridge);
        candidate.Platform.ShouldBe("Nintendo 64");
        candidate.Year.ShouldBe(1996);
        candidate.Creators.ShouldBe(["Nintendo EAD"]);
        candidate.Edition.ShouldBe("Player's Choice");
        candidate.Identifiers.ShouldBe([id]);
    }

    [Fact]
    public void A_cleaned_name_and_a_provider_release_score_end_to_end()
    {
        // "Alien 1979 DVD" -> NameCleaner -> scorer, against two provider results: the whole barcode path, pure.
        var cleaned = NameCleaner.Clean("Alien 1979 DVD");
        var read = new ReadItem { Title = cleaned.Title, Platform = cleaned.Platform, Format = cleaned.Format, Year = cleaned.Year };
        var alien = new ProviderRelease
        {
            Provider = "tmdb",
            ExternalId = "348",
            Title = "Alien",
            Kind = WorkKind.Film,
            Format = ReleaseFormat.Dvd,
            Year = 1979,
        };
        var aliens = new ProviderRelease
        {
            Provider = "tmdb",
            ExternalId = "679",
            Title = "Aliens",
            Kind = WorkKind.Film,
            Format = ReleaseFormat.Dvd,
            Year = 1986,
        };

        var result = MatchScorer.Evaluate(
            read,
            [aliens.ToCandidate(), alien.ToCandidate()],
            new ScoringOptions().Profile(ScoringProfileKind.BarcodeProduct));

        result.Outcome.ShouldBe(MatchOutcome.Matched);
        result.Ranked[0].Release.Key.ShouldBe("tmdb:348");
    }
}
